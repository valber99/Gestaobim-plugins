<#
.SYNOPSIS
  Compila o GB DWG Export para o Revit 2025, 2026 e 2027 e gera o pacote de instalacao (.zip e .exe).
  O pacote leva o cadcraft-cli ORIGINAL, sem modificacao, baixado de uma versao fixa e conferido por checksum.
#>
param(
    [string]$Configuration = 'Release',
    [int[]]$Years = @(2025, 2026, 2027),
    # Versao do CADCraft embutida e o SHA-256 do ZIP portatil de Windows dessa versao (SHA256SUMS.txt da release).
    [string]$CadcraftVersion = '0.20.1',
    [string]$CadcraftSha256 = '76904b4accff32fbc995adc9bb0137ecd982aa403cf79a281e3f10b48ca14778'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'src\GBDwgExport\GBDwgExport.csproj'
[xml]$xml = Get-Content $project
$version = ($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'Version nao encontrada no csproj.' }

# 1) O cadcraft-cli original: baixa uma vez, confere o checksum, extrai so o CLI e os avisos de licenca.
$cache = Join-Path $PSScriptRoot 'cache'
New-Item -ItemType Directory -Force $cache | Out-Null
$cadcraftZip = Join-Path $cache "cadcraft-$CadcraftVersion-windows-x64-portable.zip"
if (-not (Test-Path $cadcraftZip)) {
    $url = "https://github.com/storytold/cadcraft/releases/download/v$CadcraftVersion/cadcraft-$CadcraftVersion-windows-x64-portable.zip"
    Write-Host "Baixando o CADCraft $CadcraftVersion..."
    Invoke-WebRequest -Uri $url -OutFile $cadcraftZip -UseBasicParsing
}
$actual = (Get-FileHash $cadcraftZip -Algorithm SHA256).Hash.ToLower()
if ($actual -ne $CadcraftSha256.ToLower()) {
    Remove-Item $cadcraftZip -Force
    throw "Checksum do CADCraft nao confere (esperado $CadcraftSha256, obtido $actual)."
}

$name = "GBDwgExport-$version"
$stage = Join-Path $root "dist\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

$cadcraftStage = Join-Path $stage 'tools\cadcraft'
New-Item -ItemType Directory -Force $cadcraftStage | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($cadcraftZip)
try {
    foreach ($entry in $archive.Entries) {
        $leaf = $entry.Name
        # Fica so o CLI (sem a versao com janela) e os arquivos de licenca e atribuicao exigidos.
        if ($leaf -eq 'cadcraft-cli.exe' -or $leaf -match '^(LICENSE-.*|NOTICE|ATTRIBUTION\.md|OFL-.*\.txt)$') {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $cadcraftStage $leaf), $true)
        }
    }
} finally { $archive.Dispose() }
if (-not (Test-Path (Join-Path $cadcraftStage 'cadcraft-cli.exe'))) { throw 'cadcraft-cli.exe nao esta no pacote do CADCraft.' }
$cliVersion = & (Join-Path $cadcraftStage 'cadcraft-cli.exe') --version
Write-Host "Motor de mesclagem: $cliVersion"

# 2) O add-in, uma DLL por versao do Revit.
foreach ($year in $Years) {
    Write-Host "Compilando para o Revit $year..."
    dotnet build $project -c $Configuration "-p:RevitVersion=$year" --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar para o Revit $year" }
    $out = Join-Path $root "src\GBDwgExport\bin\$Configuration\$year"
    $payload = Join-Path $stage "$year\GBDwgExport"
    New-Item -ItemType Directory -Force $payload | Out-Null
    Copy-Item (Join-Path $out 'GBDwgExport.dll') $payload
}

foreach ($file in 'Install.ps1', 'Uninstall.ps1', 'Instalar.bat', 'Desinstalar.bat') {
    Copy-Item (Join-Path $root "installer\$file") $stage
}
Copy-Item (Join-Path $root 'installer\GBDwgExport.addin') $stage
Copy-Item (Join-Path $root 'LICENSE') $stage

$readme = @"
GB DWG Export $version
Exporta folhas e vistas do Revit para DWG/DXF e junta varias folhas num so desenho (Gestao BIM)

Nao precisa de AutoCAD, de pyRevit nem de Python.

INSTALAR
  1. Feche o Revit.
  2. De dois cliques em Instalar.bat.
  3. Abra o Revit: o painel "Exportar DWG" aparece na aba "Gestao BIM".
  Na primeira vez o Revit pergunta se confia no add-in: escolha "Sempre carregar".

DESINSTALAR
  Dois cliques em Desinstalar.bat. Suas configuracoes continuam em %APPDATA%\GestaoBIM\DwgExport.

REVIT SUPORTADO
  2025, 2026 e 2027 (Windows).

MOTOR DE MESCLAGEM
  A mesclagem de varias folhas num so DWG usa o cadcraft-cli $CadcraftVersion, SEM modificacao
  (github.com/storytold/cadcraft, licenca MIT ou Apache-2.0, escolha sua). Ele e instalado em
  %APPDATA%\GestaoBIM\Tools\cadcraft junto com os avisos de licenca (LICENSE-*, NOTICE, ATTRIBUTION.md, OFL-*).
  O cadcraft e o ArtCraft sao marcas de seus donos, sem relacao com o Gestao BIM.

CODIGO-FONTE E LICENCA
  https://github.com/valber99/Gestaobim-plugins  -  AGPL-3.0 (arquivo LICENSE)
"@
[IO.File]::WriteAllText((Join-Path $stage 'LEIA-ME.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))

$zip = Join-Path $root "dist\$name.zip"
New-ZipWithChecksum -Stage $stage -Zip $zip
New-LargeSetupExe -Zip $zip -Exe (Join-Path $root "dist\GBDwgExport-Setup-$version.exe") -Slug 'gb-dwg-export'
