<#
.SYNOPSIS
  Compila o add-in para o Revit 2025, 2026 e 2027 e gera o pacote de instalacao (.zip).
#>
param(
    [string]$Configuration = 'Release',
    [int[]]$Years = @(2025, 2026, 2027)
)
$ErrorActionPreference = 'Stop'

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
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $name.zip`n")
Write-Host "Pacote: $zip"
Write-Host "SHA256: $hash"

# Instalador .exe autoextraivel (IExpress, ja vem no Windows): extrai o zip e roda o Install.ps1.
$iex = Join-Path $root "dist\iexpress-$version"
if (Test-Path $iex) { Remove-Item $iex -Recurse -Force }
New-Item -ItemType Directory -Force $iex | Out-Null
Copy-Item $zip (Join-Path $iex 'payload.zip')
$setup = @'
@echo off
chcp 65001 >nul
title GB Material Studio - Instalador
echo %date% %time% instalador iniciado>> "%TEMP%\gb-material-studio-setup.log"
powershell -NoProfile -Command "if (Get-Process -Name Revit -ErrorAction SilentlyContinue) { exit 1 } else { exit 0 }"
if errorlevel 1 (
  echo %date% %time% revit aberto, instalacao recusada>> "%TEMP%\gb-material-studio-setup.log"
  echo O Revit esta aberto. Feche o Revit e execute este instalador de novo.
  echo.
  pause
  exit /b 1
)
set "T=%TEMP%\GBMaterialStudio-setup"
if exist "%T%" rmdir /s /q "%T%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '%~dp0payload.zip' -DestinationPath '%T%' -Force; & '%T%\Install.ps1'"
echo.
pause
'@
[IO.File]::WriteAllText((Join-Path $iex 'setup.cmd'), $setup.Replace("`r`n","`n").Replace("`n","`r`n"), (New-Object System.Text.UTF8Encoding($false)))
$exe = Join-Path $root "dist\GBMaterialStudio-Setup-$version.exe"
if (Test-Path $exe) { Remove-Item $exe -Force }
$sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$exe
FriendlyName=GB Material Studio $version
AppLaunched=setup.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[Strings]
FILE0="setup.cmd"
FILE1="payload.zip"
[SourceFiles]
SourceFiles0=$iex\
[SourceFiles0]
%FILE0%=
%FILE1%=
"@
$sedFile = Join-Path $iex 'setup.sed'
[IO.File]::WriteAllText($sedFile, $sed, [Text.Encoding]::ASCII)
& "$env:WINDIR\System32\iexpress.exe" /N /Q $sedFile | Out-Null
$deadline = (Get-Date).AddSeconds(60)
while (-not (Test-Path $exe) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
if (-not (Test-Path $exe)) { throw 'IExpress nao gerou o instalador .exe.' }
$exeHash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
[IO.File]::WriteAllText("$exe.sha256", "$exeHash  $(Split-Path $exe -Leaf)`n")
Write-Host "Instalador: $exe"
Write-Host "SHA256 exe: $exeHash"
