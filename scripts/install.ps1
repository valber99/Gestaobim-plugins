param(
    [switch]$RemoveLegacy
)
$ErrorActionPreference = 'Stop'

$source = (Resolve-Path (Join-Path $PSScriptRoot '..\GBMaterialStudio.extension')).Path
$extensions = Join-Path $env:APPDATA 'pyRevit\Extensions'
$target = Join-Path $extensions 'GBMaterialStudio.extension'
$legacy = Join-Path $extensions 'ValberMaterialStudio.extension'
$backups = Join-Path $env:APPDATA 'pyRevit'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

if (-not (Test-Path $extensions)) { New-Item -ItemType Directory $extensions | Out-Null }

if (Test-Path $target) {
    $backup = Join-Path $backups "GBMaterialStudio-backup-$stamp"
    Copy-Item $target $backup -Recurse
    Write-Host "Backup da instalacao atual: $backup"
}

robocopy $source $target /MIR /R:1 /W:1 /XD tools __pycache__ /XF *.pyc /NFL /NDL /NJH /NJS | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Falha ao copiar a extensao (robocopy codigo $LASTEXITCODE). Feche o Revit e tente de novo." }
Write-Host "Instalado em: $target"

if (Test-Path $legacy) {
    if ($RemoveLegacy) {
        $backup = Join-Path $backups "ValberMaterialStudio-backup-$stamp"
        Copy-Item $legacy $backup -Recurse
        try {
            Remove-Item $legacy -Recurse -Force
            Write-Host "Versao antiga removida (backup em $backup)."
        } catch {
            Write-Warning "Parte da versao antiga esta em uso pelo Revit. Feche o Revit e apague $legacy."
        }
    } else {
        Write-Warning "A versao antiga ainda esta instalada ($legacy) e vai duplicar a aba Gestao BIM. Rode de novo com -RemoveLegacy."
    }
}

Write-Host 'Recarregue o pyRevit (aba pyRevit, Reload) ou reinicie o Revit.'
