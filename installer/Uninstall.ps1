<#
.SYNOPSIS
  Remove os plugins Gestao BIM deste pacote do Revit. Seus dados em %APPDATA%\GestaoBIM nao sao apagados
  (favoritos, biblioteca e configuracoes).
#>
param([int[]]$Versions = @(2025, 2026, 2027))
$ErrorActionPreference = 'Stop'
$addins = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'

foreach ($manifest in @(Get-ChildItem $PSScriptRoot -Filter '*.addin' -File)) {
    [xml]$xml = Get-Content $manifest.FullName
    $folderName = Split-Path ($xml.RevitAddIns.AddIn.Assembly) -Parent
    $product = [IO.Path]::GetFileNameWithoutExtension($manifest.Name)
    foreach ($year in $Versions) {
        $installedManifest = Join-Path $addins "$year\$($manifest.Name)"
        $folder = Join-Path $addins "$year\$folderName"
        if (Test-Path $installedManifest) { Remove-Item $installedManifest -Force; Write-Host "$product - Revit $year : manifesto removido" }
        if (Test-Path $folder) {
            try { Remove-Item $folder -Recurse -Force; Write-Host "$product - Revit $year : arquivos removidos" }
            catch { Write-Warning "$product - Revit $year : arquivos em uso. Feche o Revit e rode de novo." }
        }
    }
}

# O motor de mesclagem so e usado pelo GB DWG Export.
if (Test-Path (Join-Path $PSScriptRoot 'GBDwgExport.addin')) {
    $cadcraft = Join-Path $env:APPDATA 'GestaoBIM\Tools\cadcraft'
    if (Test-Path $cadcraft) {
        try { Remove-Item $cadcraft -Recurse -Force; Write-Host 'Motor de mesclagem (cadcraft-cli) removido.' }
        catch { Write-Warning 'Nao consegui remover o cadcraft-cli (em uso?).' }
    }
}
Write-Host 'Seus dados continuam em %APPDATA%\GestaoBIM.'
