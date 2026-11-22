<#
.SYNOPSIS
  Compila o GB Material Studio para o Revit 2025, 2026 e 2027 e gera o pacote de instalacao (.zip e .exe).
#>
param(
    [string]$Configuration = 'Release',
    [int[]]$Years = @(2025, 2026, 2027)
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'src\GBMaterialStudio\GBMaterialStudio.csproj'
[xml]$xml = Get-Content $project
$version = ($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'Version nao encontrada no csproj.' }

$name = "GBMaterialStudio-$version"
$stage = Join-Path $root "dist\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

foreach ($year in $Years) {
    Write-Host "Compilando para o Revit $year..."
    dotnet build $project -c $Configuration "-p:RevitVersion=$year" --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar para o Revit $year" }
    $out = Join-Path $root "src\GBMaterialStudio\bin\$Configuration\$year"
    $payload = Join-Path $stage "$year\GBMaterialStudio"
    New-Item -ItemType Directory -Force $payload | Out-Null
    foreach ($file in 'GBMaterialStudio.dll', 'eyedropper.cur', 'gestaobim.png') {
        Copy-Item (Join-Path $out $file) $payload
    }
}

foreach ($file in 'GBMaterialStudio.addin', 'Install.ps1', 'Uninstall.ps1', 'Instalar.bat', 'Desinstalar.bat') {
    Copy-Item (Join-Path $root "installer\$file") $stage
}
Copy-Item (Join-Path $root 'LICENSE') $stage

$readme = @"
GB Material Studio $version
Estudio de materiais para o Revit (Gestao BIM)

Nao precisa de pyRevit nem de Python.

INSTALAR
  1. Feche o Revit.
  2. De dois cliques em Instalar.bat.
  3. Abra o Revit: a aba "Gestao BIM" aparece sozinha.
  Na primeira vez o Revit pergunta se confia no add-in: escolha "Sempre carregar".

DESINSTALAR
  Dois cliques em Desinstalar.bat. Seus favoritos e a biblioteca de materiais
  continuam em %APPDATA%\GestaoBIM\MaterialStudio.

REVIT SUPORTADO
  2025, 2026 e 2027 (Windows).

CODIGO-FONTE E LICENCA
  https://github.com/valber99/Gestaobim-plugins  -  AGPL-3.0 (arquivo LICENSE)
"@
[IO.File]::WriteAllText((Join-Path $stage 'LEIA-ME.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))

$zip = Join-Path $root "dist\$name.zip"
New-ZipWithChecksum -Stage $stage -Zip $zip
New-LargeSetupExe -Zip $zip -Exe (Join-Path $root "dist\GBMaterialStudio-Setup-$version.exe") -Slug 'gb-material-studio'
