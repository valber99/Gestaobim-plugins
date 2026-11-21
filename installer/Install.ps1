<#
.SYNOPSIS
  Instala o GB Material Studio no Revit. Nao precisa de pyRevit nem de Python.
.PARAMETER Versions
  Anos do Revit (2025, 2026, 2027). Sem o parametro, instala em todas as versoes encontradas.
.PARAMETER KeepPyRevitEdition
  Nao mover a edicao antiga em pyRevit (ela duplicaria a aba Gestao BIM).
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

if (-not $Versions) { $Versions = $supported | Where-Object { Test-RevitInstalled $_ } }
if (-not $Versions) { throw 'Nenhum Revit 2025, 2026 ou 2027 encontrado. Use -Versions 2027 para forcar.' }

$manifest = Join-Path $here 'GBMaterialStudio.addin'
foreach ($year in $Versions) {
    $payload = Join-Path $here "$year\GBMaterialStudio"
    if (-not (Test-Path $payload)) { Write-Warning "Sem arquivos para o Revit $year neste pacote. Pulando."; continue }
    $target = Join-Path $addins "$year\GBMaterialStudio"
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item (Join-Path $payload '*') $target -Recurse -Force
    Copy-Item $manifest (Join-Path $addins "$year\GBMaterialStudio.addin") -Force
    Get-ChildItem $target -Recurse -File | Unblock-File
    Write-Host "Revit $year : instalado em $target"
}

if (-not $KeepPyRevitEdition) {
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