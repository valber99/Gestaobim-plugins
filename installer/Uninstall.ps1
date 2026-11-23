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
# O motor de busca por IA (gbfam.exe e os modelos) so e usado pelo GB Familias.
if (Test-Path (Join-Path $PSScriptRoot 'GBFamilias.addin')) {
    $gbfam = Join-Path $env:APPDATA 'GestaoBIM\Tools\gbfam'
    if (Test-Path $gbfam) {
        try { Remove-Item $gbfam -Recurse -Force; Write-Host 'Motor de busca por IA (gbfam) removido.' }
        catch { Write-Warning 'Nao consegui remover o gbfam (em uso? feche o Revit).' }
    }
    # O catalogo (banco com previews e dados de busca) vem no instalador e e recriado por ele.
    $catalogFolder = Join-Path $env:LOCALAPPDATA 'GestaoBIM\Familias'
    foreach ($f in 'catalog.sqlite', 'catalog.sqlite.anterior', 'catalog.version') {
        $file = Join-Path $catalogFolder $f
        if (Test-Path $file) { try { Remove-Item $file -Force } catch { Write-Warning "Nao consegui remover $f (em uso? feche o Revit)." } }
    }
    Write-Host 'Catalogo removido. As familias baixadas (na pasta que voce escolheu) e a biblioteca do modo local NAO foram apagadas.'
}
# O gateway de IA (CLIProxyAPI) so e usado pelo GB Cotas e pelo GB Render. As contas conectadas (auth) ficam: sao do usuario.
if ((Test-Path (Join-Path $PSScriptRoot 'GBCotas.addin')) -or (Test-Path (Join-Path $PSScriptRoot 'GBRender.addin'))) {
    $proxy = Join-Path $env:APPDATA 'GestaoBIM\Tools\cliproxyapi'
    if (Test-Path $proxy) {
        try { Remove-Item $proxy -Recurse -Force; Write-Host 'Gateway de IA (CLIProxyAPI) removido.' }
        catch { Write-Warning 'Nao consegui remover o gateway de IA (em uso? feche o Revit).' }
    }
    # Configs de sessoes antigas do gateway (tokens que ja nao valem) nao precisam ficar.
    $run = Join-Path $env:LOCALAPPDATA 'GestaoBIM\gbai\run'
    if (Test-Path $run) { try { Remove-Item $run -Recurse -Force } catch { } }
}
Write-Host 'Seus dados continuam em %APPDATA%\GestaoBIM.'
