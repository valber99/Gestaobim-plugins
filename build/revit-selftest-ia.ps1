<#
.SYNOPSIS
  Testa o GB Cotas ou o GB Render DENTRO de um Revit de verdade (2025, 2026 ou 2027), sem interface, sem rede e sem tocar em projeto seu.
.DESCRIPTION
  Compila o plugin para o ano, instala no Revit desse ano, copia os projetos de teste para uma pasta temporaria e abre uma sessao propria
  do Revit com a variavel de autoverificacao ligada (src/GBCotas/Revit/SelfTest.cs ou src/GBRender/Revit/SelfTest.cs).
    Cotas : extrai as referencias de plantas, planeja pelas regras, posiciona, cria as cotas e confere 0 excecoes, 0 colisoes e 100% das
            cadeias como Dimension nativa (grava o autodim_audit.json de cada planta).
    Render: extrai a geometria 3D (CustomExporter), monta as quatro camadas e o manifesto e confere 4 imagens do mesmo tamanho, 100% das
            cores da mascara no manifesto e a camera (grava render_bundle.zip e os PNG para conferir a olho).
  Os projetos sao abertos desanexados e nunca salvos. Fecha SO a instancia que ele abriu. Outros anos do Revit podem continuar abertos.
.PARAMETER Plugin
  Cotas ou Render.
.PARAMETER Year
  Ano do Revit (2025, 2026 ou 2027). O Revit desse ano precisa estar fechado.
.PARAMETER Models
  Projetos .rvt para testar (no formato do ano ou mais antigos).
#>
param(
    [Parameter(Mandatory)][ValidateSet('Cotas', 'Render')][string]$Plugin,
    [Parameter(Mandatory)][ValidateSet(2025, 2026, 2027)][int]$Year,
    [Parameter(Mandatory)][string[]]$Models,
    [int]$TimeoutMinutes = 30,
    [string]$WorkDir = (Join-Path $env:TEMP "gbia-selftest-$Plugin-$Year"),
    [switch]$SkipInstall
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$name = "GB$Plugin"

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
    Write-Host "Compilando o $name..."
    & dotnet build (Join-Path $root "src\$name\$name.csproj") -c Release "-p:RevitVersion=$Year" --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar o $name." }
    New-Item -ItemType Directory -Force (Join-Path $addins $name) | Out-Null
    Copy-Item (Join-Path $root "src\$name\bin\Release\$Year\*") (Join-Path $addins $name) -Recurse -Force
    Copy-Item (Join-Path $root "installer\$name.addin") $addins -Force
    Get-ChildItem (Join-Path $addins $name) -Recurse -File | Unblock-File
}

$copies = foreach ($model in $Models) {
    $target = Join-Path "$work\modelos" (Split-Path $model -Leaf)
    Copy-Item $model $target
    $target
}

$upper = $Plugin.ToUpper()
Set-Item "env:GB_${upper}_SELFTEST" "$work\saida"
Set-Item "env:GB_${upper}_SELFTEST_MODELS" ($copies -join ';')
$env:GB_COTAS_HOME = "$work\config"
$env:GB_AI_NO_UPDATE_CHECK = '1'
$env:GB_DWG_EXPORT_NO_UPDATE_CHECK = '1'
$env:GB_FAMILIAS_NO_UPDATE_CHECK = '1'
$env:GB_MATERIAL_STUDIO_NO_UPDATE_CHECK = '1'
$report = "$work\saida\relatorio.txt"
$env:TEMP = "$work\tmp"
$env:TMP = "$work\tmp"

$process = Start-Process $revitExe -PassThru
Write-Host "Revit aberto (PID $($process.Id)). Aguardando o relatorio (ate $TimeoutMinutes min)..."

# Quem responde ao aviso "complemento nao assinado" e um processo a parte: uma chamada de automacao a um Revit ocupado (abrindo
# projeto) pode ficar parada por minutos, e no laco principal isso impedia de ver o fim do relatorio.
$clicker = Start-Job -ArgumentList $process.Id, $report -ScriptBlock {
    param([int]$processId, [string]$report)
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    Add-Type -Namespace GbSelfTest -Name User32 -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);'
    $desktop = [Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    $dialog = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ClassNameProperty, '#32770')
    # Os avisos so aparecem enquanto os plugins carregam, antes do relatorio comecar.
    while (-not (Test-Path $report) -and (Get-Process -Id $processId -ErrorAction SilentlyContinue)) {
        try {
            # O aviso e uma caixa de dialogo (#32770) de nivel de cima ou filha direta da janela principal: so olha essas.
            $windows = foreach ($top in $desktop.FindAll([Windows.Automation.TreeScope]::Children, $cond)) {
                if ($top.Current.ClassName -eq '#32770') { $top } else { $top.FindAll([Windows.Automation.TreeScope]::Children, $dialog) }
            }
            foreach ($w in $windows) {
                # Os botoes sao "command links" (CCPushButton): a automacao os mostra como Pane, sem Invoke. Cada um tem a propria
                # janela, entao o clique vai como BM_CLICK (PostMessage, para nao esperar o Revit).
                foreach ($b in $w.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
                    if ($b.Current.Name -match 'Sempre carregar|Always Load|Carregar sempre' -and $b.Current.NativeWindowHandle -ne 0) {
                        [void][GbSelfTest.User32]::PostMessage([IntPtr]$b.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
                        '  (confirmei "Sempre carregar" no aviso de seguranca do Revit)'
                    }
                }
            }
        } catch { }
        Start-Sleep -Seconds 2
    }
}

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$done = $false
while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
    Start-Sleep -Seconds 3
    if ((Test-Path $report) -and (Select-String -Path $report -Pattern '^\S+ FIM ' -Quiet)) { $done = $true; break }
}

if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
Stop-Job $clicker -ErrorAction SilentlyContinue
Receive-Job $clicker -ErrorAction SilentlyContinue | Select-Object -Unique | ForEach-Object { Write-Host $_ }
Remove-Job $clicker -Force -ErrorAction SilentlyContinue
Write-Host ''
if (Test-Path $report) { Get-Content $report -Encoding UTF8 } else { Write-Warning 'O plugin nao escreveu relatorio (o Revit abriu? o plugin carregou?).' }
$failed = if (Test-Path $report) { @(Select-String -Path $report -Pattern '^\S+ FALHOU ' -CaseSensitive).Count } else { 1 }
if (-not $done) { Write-Warning "O relatorio nao terminou em $TimeoutMinutes minutos." }
Write-Host ''
Write-Host "Saida: $work\saida"
if ($done -and $failed -eq 0) { Write-Host "Revit $Year ($name): TUDO PASSOU." -ForegroundColor Green; exit 0 }
Write-Host "Revit $Year ($name): $failed verificacao(oes) falharam ou o teste nao terminou." -ForegroundColor Red
exit 1
