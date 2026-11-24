<#
.SYNOPSIS
  Testa o GB Familias DENTRO de um Revit de verdade (2025, 2026 ou 2027), sem interface e sem tocar em projeto seu.
.DESCRIPTION
  Compila o plugin para o ano, instala no Revit desse ano (substitui a versao instalada: e a atualizacao normal),
  abre o Revit com dados ISOLADOS (configuracao, catalogo e login de teste numa pasta temporaria) e uma variavel de ambiente
  que faz o plugin rodar a autoverificacao (src/GBFamilias/Revit/SelfTest.cs): login salvo, catalogo, busca de texto e de IA,
  baixar uma familia real do servidor, carregar e posicionar num projeto NOVO em memoria, reaproveitar, simular a falta de
  internet e a sessao apagada. Escreve um relatorio, fecha SO a instancia que ele abriu e apaga o usuario de teste.
.PARAMETER Year
  Ano do Revit (2025, 2026 ou 2027).
.PARAMETER TimeoutMinutes
  Quanto esperar o relatorio (padrao 15; o primeiro download e o carregamento dos modelos de IA levam alguns minutos).
.NOTES
  Exige o Revit FECHADO (o teste instala o plugin e abre uma sessao propria) e o diretorio administrativo do servidor
  (informado em -ServerRepo ou na variavel GBFAM_SERVER_REPO) com o catalogo gerado e script de token de teste. Nunca imprime segredos.
#>
param(
    [Parameter(Mandatory)][ValidateSet(2025, 2026, 2027)][int]$Year,
    [int]$TimeoutMinutes = 15,
    [string]$ServerRepo = $env:GBFAM_SERVER_REPO,
    [switch]$DryRun,
    # Testa o que ja esta instalado (por exemplo, pelos instaladores .exe da release), sem compilar nem copiar nada.
    [switch]$SkipInstall
)
$ErrorActionPreference = 'Stop'
if (-not $ServerRepo) { throw 'Informe -ServerRepo <pasta> ou defina a variavel de ambiente GBFAM_SERVER_REPO.' }
$root = Split-Path $PSScriptRoot

if (Get-Process Revit -ErrorAction SilentlyContinue) { throw 'O Revit esta aberto. Salve e feche o Revit antes (o teste abre uma sessao propria e instala o plugin).' }

# --- onde esta o Revit.exe desse ano
$install = (Get-ChildItem "HKLM:\SOFTWARE\Autodesk\Revit\$Year" -ErrorAction SilentlyContinue | ForEach-Object { (Get-ItemProperty $_.PSPath).InstallationLocation } | Where-Object { $_ } | Select-Object -First 1)
$candidates = @()
if ($install) { $candidates += Join-Path $install 'Revit.exe' }
if ($env:REVIT_INSTALL_ROOT) { $candidates += (Join-Path $env:REVIT_INSTALL_ROOT "Revit $Year\Revit.exe"), (Join-Path $env:REVIT_INSTALL_ROOT "$Year\Revit $Year\Revit.exe") }
$candidates += "C:\Program Files\Autodesk\Revit $Year\Revit.exe"
$revitExe = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $revitExe) { throw "Revit.exe do $Year nao encontrado ($revitExe)." }

$work = Join-Path $env:TEMP "gbfam-selftest-$Year"
$report = Join-Path $work 'relatorio.txt'
$tokenFile = Join-Path $work 'token.json'
Write-Host "Revit $Year : $revitExe"
Write-Host "Pasta de teste (isolada): $work"
if ($DryRun) { Write-Host '(simulacao: nada foi instalado nem aberto)'; return }

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force "$work\home", "$work\local", "$work\baixadas" | Out-Null

# --- 1) compila e instala o plugin desse ano
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year"
if ($SkipInstall) {
    Write-Host 'Usando os plugins ja instalados (sem compilar).'
    foreach ($p in 'GBFamilias', 'GBMaterialStudio', 'GBDwgExport') {
        $dll = Join-Path $addins "$p\$p.dll"
        if (-not (Test-Path $dll)) { throw "$p nao esta instalado no Revit $Year." }
        Write-Host "  $p $((Get-Item $dll).VersionInfo.ProductVersion)"
    }
} else {
Write-Host 'Compilando o plugin...'
& dotnet build (Join-Path $root 'src\GBFamilias\GBFamilias.csproj') -c Release "-p:RevitVersion=$Year" --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Falha ao compilar o plugin.' }
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year"
New-Item -ItemType Directory -Force (Join-Path $addins 'GBFamilias') | Out-Null
Copy-Item (Join-Path $root "src\GBFamilias\bin\Release\$Year\*") (Join-Path $addins 'GBFamilias') -Recurse -Force
Copy-Item (Join-Path $root 'installer\GBFamilias.addin') $addins -Force
Get-ChildItem (Join-Path $addins 'GBFamilias') -Recurse -File | Unblock-File
# Os outros dois plugins tambem, na versao deste codigo: o botao "Sobre" e um so para os tres (src/GBShared).
foreach ($other in 'GBMaterialStudio', 'GBDwgExport') {
    & dotnet build (Join-Path $root "src\$other\$other.csproj") -c Release "-p:RevitVersion=$Year" --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar o $other." }
    New-Item -ItemType Directory -Force (Join-Path $addins $other) | Out-Null
    Copy-Item (Join-Path $root "src\$other\bin\Release\$Year\*") (Join-Path $addins $other) -Recurse -Force
    Copy-Item (Join-Path $root "installer\$other.addin") $addins -Force
    Get-ChildItem (Join-Path $addins $other) -Recurse -File | Unblock-File
}
}

# --- 2) dados isolados: configuracao e o catalogo que viria no instalador
$settings = @{ mode = 'server'; downloads_folder = "$work\baixadas"; semantic_enabled = $true; cache_limit_gb = 10 }
# Instalado pela release: o motor de IA e os modelos sao os do instalador (%APPDATA%\GestaoBIM\Tools\gbfam).
if (-not $SkipInstall) { $settings.gbfam_exe = (Join-Path $root 'tools\gbfam\target\release\gbfam.exe') }
$settings | ConvertTo-Json | Set-Content "$work\home\settings.json" -Encoding UTF8
. (Join-Path $PSScriptRoot 'common.ps1')
$installedCatalog = Join-Path $env:LOCALAPPDATA 'GestaoBIM\Familias\catalog.sqlite'
if ($SkipInstall -and (Test-Path $installedCatalog)) {
    # O catalogo que o instalador colocou (copia: o teste nunca mexe no original).
    Copy-Item $installedCatalog "$work\local\catalog.sqlite"
    $versionFile = Join-Path (Split-Path $installedCatalog) 'catalog.version'
    if (Test-Path $versionFile) { Copy-Item $versionFile "$work\local\catalog.version" }
    Write-Host "Catalogo do instalador: versao $(Get-Content $versionFile -ErrorAction SilentlyContinue)"
} else {
    $manifest = Get-Content (Join-Path $ServerRepo 'catalog-out\manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    Expand-GzipFile -Path (Join-Path $ServerRepo "catalog-out\$($manifest.snapshot.file)") -Destination "$work\local\catalog.sqlite"
}

# --- 3) usuario de teste no servidor (token JWT real)
& python (Join-Path $ServerRepo 'ops\mint_token.py') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Nao consegui criar o usuario de teste no servidor.' }
Move-Item (Join-Path $ServerRepo '.local\ui-token.json') $tokenFile -Force

# --- 4) abre o Revit com a autoverificacao ligada
$env:GB_FAMILIAS_HOME = "$work\home"
$env:GB_FAMILIAS_LOCAL = "$work\local"
$env:GB_FAMILIAS_SELFTEST = $report
$env:GB_FAMILIAS_SELFTEST_TOKEN = $tokenFile
$env:GB_FAMILIAS_NO_UPDATE_CHECK = '1'
$env:GB_DWG_EXPORT_NO_UPDATE_CHECK = '1'
$env:GB_MATERIAL_STUDIO_NO_UPDATE_CHECK = '1'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$process = Start-Process $revitExe -PassThru
Write-Host "Revit aberto (PID $($process.Id)). Aguardando o relatorio (ate $TimeoutMinutes min)..."

# Na primeira vez o Revit pergunta se confia no add-in (assinatura): escolhe "Sempre carregar".
function Confirm-AddinDialog([int]$processId) {
    # A automacao da interface pode falhar enquanto o Revit cria/destroi janelas (COMException): tenta de novo no proximo ciclo.
    try { Confirm-AddinDialogCore $processId } catch { }
}
function Confirm-AddinDialogCore([int]$processId) {
    $root = [Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
    foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, $cond)) {
        foreach ($b in $w.FindAll([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)))) {
            if ($b.Current.Name -match 'Sempre carregar|Always Load|Carregar sempre') {
                try { $b.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Write-Host '  (confirmei "Sempre carregar" no aviso de seguranca do Revit)' } catch { }
            }
        }
    }
}

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
$done = $false
while ((Get-Date) -lt $deadline -and -not $process.HasExited) {
    Start-Sleep -Seconds 3
    Confirm-AddinDialog $process.Id
    if ((Test-Path $report) -and (Select-String -Path $report -Pattern '^\S+ FIM ' -Quiet)) { $done = $true; break }
}

# --- 5) resultado, limpeza (so a instancia que este script abriu)
if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
& python (Join-Path $ServerRepo 'ops\mint_token.py') delete | Out-Null
Remove-Item $tokenFile -Force -ErrorAction SilentlyContinue
Write-Host ''
if (Test-Path $report) { Get-Content $report -Encoding UTF8 } else { Write-Warning 'O plugin nao escreveu relatorio (o Revit abriu? o plugin carregou?).' }
$failed = if (Test-Path $report) { @(Select-String -Path $report -Pattern '^\S+ FALHOU ' -CaseSensitive).Count } else { 1 }
if (-not $done) { Write-Warning "O relatorio nao terminou em $TimeoutMinutes minutos." }
Write-Host ''
if ($done -and $failed -eq 0) { Write-Host "Revit ${Year}: TUDO PASSOU." -ForegroundColor Green; exit 0 }
Write-Host "Revit ${Year}: $failed verificacao(oes) falharam ou o teste nao terminou." -ForegroundColor Red
exit 1
