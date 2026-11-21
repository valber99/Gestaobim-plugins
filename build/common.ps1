# Funcoes compartilhadas pelos scripts de empacotamento (package.ps1 e package-dwg.ps1).

function New-ZipWithChecksum {
    param([string]$Stage, [string]$Zip)
    if (Test-Path $Zip) { Remove-Item $Zip -Force }
    Compress-Archive -Path (Join-Path $Stage '*') -DestinationPath $Zip
    $hash = (Get-FileHash $Zip -Algorithm SHA256).Hash.ToLower()
    [IO.File]::WriteAllText("$Zip.sha256", "$hash  $(Split-Path $Zip -Leaf)`n")
    Write-Host "Pacote: $Zip"
    Write-Host "SHA256: $hash"
}

<#
.SYNOPSIS
  Instalador .exe autoextraivel (IExpress, ja vem no Windows): recusa instalar com o Revit aberto,
  extrai o .zip e roda o Install.ps1. Registra o que fez em %TEMP%\<Slug>-setup.log.
#>
function New-SetupExe {
    param(
        [string]$Zip,       # pacote .zip com o Install.ps1 na raiz
        [string]$Exe,       # caminho do .exe a gerar
        [string]$Title,     # nome mostrado ao usuario, por exemplo "GB DWG Export 0.1.0"
        [string]$Slug       # nome curto sem espacos, por exemplo "gb-dwg-export"
    )
    $iex = Join-Path (Split-Path $Exe) "iexpress-$Slug"
    if (Test-Path $iex) { Remove-Item $iex -Recurse -Force }
    New-Item -ItemType Directory -Force $iex | Out-Null
    Copy-Item $Zip (Join-Path $iex 'payload.zip')

    $setup = @"
@echo off
chcp 65001 >nul
title $Title - Instalador
echo %date% %time% instalador iniciado>> "%TEMP%\$Slug-setup.log"
powershell -NoProfile -Command "if (Get-Process -Name Revit -ErrorAction SilentlyContinue) { exit 1 } else { exit 0 }"
if errorlevel 1 (
  echo %date% %time% revit aberto, instalacao recusada>> "%TEMP%\$Slug-setup.log"
  echo O Revit esta aberto. Feche o Revit e execute este instalador de novo.
  echo.
  pause
  exit /b 1
)
set "T=%TEMP%\$Slug-setup"
if exist "%T%" rmdir /s /q "%T%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath '%~dp0payload.zip' -DestinationPath '%T%' -Force; & '%T%\Install.ps1'"
echo.
pause
"@
    [IO.File]::WriteAllText((Join-Path $iex 'setup.cmd'), $setup.Replace("`r`n", "`n").Replace("`n", "`r`n"), (New-Object System.Text.UTF8Encoding($false)))

    if (Test-Path $Exe) { Remove-Item $Exe -Force }
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
TargetName=$Exe
FriendlyName=$Title
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
    $deadline = (Get-Date).AddSeconds(120)
    while (-not (Test-Path $Exe) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    if (-not (Test-Path $Exe)) { throw 'IExpress nao gerou o instalador .exe.' }
    $hash = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLower()
    [IO.File]::WriteAllText("$Exe.sha256", "$hash  $(Split-Path $Exe -Leaf)`n")
    Write-Host "Instalador: $Exe"
    Write-Host "SHA256 exe: $hash"
}
