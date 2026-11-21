<#
.SYNOPSIS
  Remove o GB Material Studio do Revit. Favoritos e biblioteca ficam em %APPDATA%\GestaoBIM e nao sao apagados.
#>
param([int[]]$Versions = @(2025, 2026, 2027))
$ErrorActionPreference = 'Stop'
$addins = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'
foreach ($year in $Versions) {
    $manifest = Join-Path $addins "$year\GBMaterialStudio.addin"
    $folder = Join-Path $addins "$year\GBMaterialStudio"
    if (Test-Path $manifest) { Remove-Item $manifest -Force; Write-Host "Revit $year : manifesto removido" }
    if (Test-Path $folder) {
        try { Remove-Item $folder -Recurse -Force; Write-Host "Revit $year : arquivos removidos" }
        catch { Write-Warning "Revit $year : arquivos em uso. Feche o Revit e rode de novo." }
    }
}
Write-Host 'Seus favoritos e a biblioteca de materiais continuam em %APPDATA%\GestaoBIM\MaterialStudio.'