<#
.SYNOPSIS
  Testa o GB DWG Export DENTRO de um Revit de verdade (2025, 2026 ou 2027), sem interface e sem tocar em projeto seu.
.DESCRIPTION
  Compila o plugin para o ano, instala no Revit desse ano, copia os projetos de teste para uma pasta temporaria e abre
  uma sessao propria do Revit com GB_DWG_EXPORT_SELFTEST ligada (src/GBDwgExport/Revit/SelfTest.cs): exporta folhas em
  cada modo de mesclagem (e com a configuracao de exportacao do proprio projeto), exporta vistas no model space, confere
  os layouts e grava um PNG de cada layout. Os projetos sao abertos desanexados e nunca salvos. Fecha SO a instancia que
  ele abriu. Outros anos do Revit podem continuar abertos.
.PARAMETER Year
  Ano do Revit (2025, 2026 ou 2027). O Revit desse ano precisa estar fechado.
.PARAMETER Models
  Projetos .rvt para testar (no formato do ano ou mais antigos).
#>
param(
    [Parameter(Mandatory)][ValidateSet(2025, 2026, 2027)][int]$Year,
    [Parameter(Mandatory)][string[]]$Models,
    [int]$TimeoutMinutes = 30,
    # Pasta do teste: copias dos projetos, saida e os temporarios do Revit (projetos grandes pedem varios GB livres).
    [string]$WorkDir = (Join-Path $env:TEMP "gbdwg-selftest-$Year"),
    [switch]$SkipInstall
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot

$install = (Get-ChildItem "HKLM:\SOFTWARE\Autodesk\Revit\$Year" -ErrorAction SilentlyContinue | ForEach-Object { (Get-ItemProperty $_.PSPath).InstallationLocation } | Where-Object { $_ } | Select-Object -First 1)
$candidates = @()
if ($install) { $candidates += Join-Path $install 'Revit.exe' }
if ($env:REVIT_INSTALL_ROOT) { $candidates += (Join-Path $env:REVIT_INSTALL_ROOT "Revit $Year\Revit.exe"), (Join-Path $env:REVIT_INSTALL_ROOT "$Year\Revit $Year\Revit.exe") }
$candidates += "C:\Program Files\Autodesk\Revit $Year\Revit.exe"
$revitExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $revitExe) { throw "Revit.exe do $Year nao encontrado." }
$sameYear = Get-Process Revit -ErrorAction SilentlyContinue | Where-Object { $_.Path -and ((Resolve-Path $_.Path).Path -eq (Resolve-Path $revitExe).Path) }
if ($sameYear) { throw "O Revit $Year esta aberto. Feche-o antes (o teste instala o plugin desse ano e abre uma sessao propria)." }

$work = $WorkDir
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force "$work\modelos", "$work\saida", "$work\config", "$work\tmp" | Out-Null
Write-Host "Revit $Year : $revitExe"
Write-Host "Pasta de teste (isolada): $work"

$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year"
if (-not $SkipInstall) {
    Write-Host 'Compilando o GB DWG Export...'
    & dotnet build (Join-Path $root 'src\GBDwgExport\GBDwgExport.csproj') -c Release "-p:RevitVersion=$Year" --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o GB DWG Export.' }
    New-Item -ItemType Directory -Force (Join-Path $addins 'GBDwgExport') | Out-Null
    Copy-Item (Join-Path $root "src\GBDwgExport\bin\Release\$Year\*") (Join-Path $addins 'GBDwgExport') -Recurse -Force
    Copy-Item (Join-Path $root 'installer\GBDwgExport.addin') $addins -Force
    Get-ChildItem (Join-Path $addins 'GBDwgExport') -Recurse -File | Unblock-File
}

# Copias: o teste nunca abre o arquivo original.
$copies = foreach ($model in $Models) {
    $target = Join-Path "$work\modelos" (Split-Path $model -Leaf)
    Copy-Item $model $target
    $target
}

$env:GB_DWG_EXPORT_SELFTEST = "$work\saida"
$env:GB_DWG_EXPORT_SELFTEST_MODELS = $copies -join ';'
$env:GB_DWG_EXPORT_HOME = "$work\config"
$env:GB_DWG_EXPORT_NO_UPDATE_CHECK = '1'
$env:GB_FAMILIAS_NO_UPDATE_CHECK = '1'
$env:GB_MATERIAL_STUDIO_NO_UPDATE_CHECK = '1'
$report = "$work\saida\relatorio.txt"
# Os temporarios do Revit (e os da exportacao) ficam na pasta do teste, no mesmo disco dela.
$env:TEMP = "$work\tmp"
$env:TMP = "$work\tmp"

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$process = Start-Process $revitExe -PassThru
Write-Host "Revit aberto (PID $($process.Id)). Aguardando o relatorio (ate $TimeoutMinutes min)..."

# Na primeira vez o Revit pergunta se confia no add-in: escolhe "Sempre carregar".
function Confirm-AddinDialog([int]$processId) {
    try {
        $desktop = [Windows.Automation.AutomationElement]::RootElement
        $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
        $buttons = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
        $main = (Get-Process -Id $processId -ErrorAction SilentlyContinue).MainWindowHandle
        foreach ($w in $desktop.FindAll([Windows.Automation.TreeScope]::Children, $cond)) {
            # So os dialogos: varrer a janela principal do Revit inteira pode levar minutos e travar o laco.
            if ($main -and $w.Current.NativeWindowHandle -eq [int64]$main) { continue }
            foreach ($b in $w.FindAll([Windows.Automation.TreeScope]::Descendants, $buttons)) {
                if ($b.Current.Name -match 'Sempre carregar|Always Load|Carregar sempre') {
                    $b.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
                    Write-Host '  (confirmei "Sempre carregar" no aviso de seguranca do Revit)'
                }
            }
        }
    } catch { }
}

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$done = $false
while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
    Start-Sleep -Seconds 3
    # O relatorio primeiro: a busca do aviso na interface do Revit pode levar minutos depois que ele abre.
    if ((Test-Path $report) -and (Select-String -Path $report -Pattern '^\S+ FIM ' -Quiet)) { $done = $true; break }
    if (-not (Test-Path $report)) { Confirm-AddinDialog $process.Id }
}

if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
Write-Host ''
if (Test-Path $report) { Get-Content $report -Encoding UTF8 } else { Write-Warning 'O plugin nao escreveu relatorio (o Revit abriu? o plugin carregou?).' }
$failed = if (Test-Path $report) { @(Select-String -Path $report -Pattern '^\S+ FALHOU ' -CaseSensitive).Count } else { 1 }
if (-not $done) { Write-Warning "O relatorio nao terminou em $TimeoutMinutes minutos." }
Write-Host ''
Write-Host "Saida (DWG e PNG de cada layout): $work\saida"
if ($done -and $failed -eq 0) { Write-Host "Revit ${Year}: TUDO PASSOU." -ForegroundColor Green; exit 0 }
Write-Host "Revit ${Year}: $failed verificacao(oes) falharam ou o teste nao terminou." -ForegroundColor Red
exit 1
