<#
.SYNOPSIS
  Instala os plugins Gestao BIM deste pacote no Revit. Nao precisa de pyRevit nem de Python.
  Serve a todos os plugins: instala cada arquivo .addin que estiver ao lado deste script.
.PARAMETER Versions
  Anos do Revit (2025, 2026, 2027). Sem o parametro, instala em todas as versoes encontradas.
.PARAMETER KeepPyRevitEdition
  Nao mover a edicao antiga em pyRevit do GB Material Studio (ela duplicaria a aba Gestao BIM).
#>
param(
    [int[]]$Versions,
    [switch]$KeepPyRevitEdition
)
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$addins = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'
$supported = 2025, 2026, 2027

function Test-RevitInstalled([int]$year) {
    return (Test-Path "HKLM:\SOFTWARE\Autodesk\Revit\Autodesk Revit $year") -or (Test-Path (Join-Path $addins $year))
}

$manifests = @(Get-ChildItem $here -Filter '*.addin' -File)
if (-not $manifests) { throw 'Nenhum arquivo .addin encontrado ao lado do instalador.' }

if (-not $Versions) { $Versions = $supported | Where-Object { Test-RevitInstalled $_ } }
if (-not $Versions) { throw 'Nenhum Revit 2025, 2026 ou 2027 encontrado. Use -Versions 2027 para forcar.' }

foreach ($manifest in $manifests) {
    # A pasta do plugin e a do <Assembly>, por exemplo "GBDwgExport\GBDwgExport.dll" -> "GBDwgExport".
    [xml]$xml = Get-Content $manifest.FullName
    $folderName = Split-Path ($xml.RevitAddIns.AddIn.Assembly) -Parent
    $product = [IO.Path]::GetFileNameWithoutExtension($manifest.Name)
    foreach ($year in $Versions) {
        $payload = Join-Path $here "$year\$folderName"
        if (-not (Test-Path $payload)) { Write-Warning "Sem arquivos de $product para o Revit $year neste pacote. Pulando."; continue }
        $target = Join-Path $addins "$year\$folderName"
        New-Item -ItemType Directory -Force $target | Out-Null
        Copy-Item (Join-Path $payload '*') $target -Recurse -Force
        Copy-Item $manifest.FullName (Join-Path $addins "$year\$($manifest.Name)") -Force
        Get-ChildItem $target -Recurse -File | Unblock-File
        Write-Host "$product - Revit $year : instalado em $target"
    }
}

# Ferramentas compartilhadas (por exemplo o cadcraft-cli do GB DWG Export): uma copia so, para todos os anos do Revit.
$tools = Join-Path $here 'tools'
if (Test-Path $tools) {
    $toolsTarget = Join-Path $env:APPDATA 'GestaoBIM\Tools'
    New-Item -ItemType Directory -Force $toolsTarget | Out-Null
    Copy-Item (Join-Path $tools '*') $toolsTarget -Recurse -Force
    Get-ChildItem $toolsTarget -Recurse -File | Unblock-File
    Write-Host "Ferramentas instaladas em $toolsTarget"
}

if (-not $KeepPyRevitEdition -and ($manifests | Where-Object Name -eq 'GBMaterialStudio.addin')) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    foreach ($old in 'GBMaterialStudio.extension', 'ValberMaterialStudio.extension') {
        $folder = Join-Path $env:APPDATA "pyRevit\Extensions\$old"
        if (Test-Path $folder) {
            $backup = Join-Path $env:APPDATA "pyRevit\$old-backup-$stamp"
            try {
                Move-Item $folder $backup
                Write-Host "Edicao antiga em pyRevit movida para $backup (evita aba duplicada)."
            } catch {
                Write-Warning "Nao consegui mover $folder (o Revit esta aberto?). Feche o Revit e rode de novo."
            }
        }
    }
}

Write-Host ''
Write-Host 'Pronto. Abra o Revit: a aba "Gestao BIM" aparece sozinha.'
Write-Host 'Na primeira vez o Revit pergunta se confia no add-in: escolha "Sempre carregar".'
