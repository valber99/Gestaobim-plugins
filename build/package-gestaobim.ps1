<#
.SYNOPSIS
  Gera o INSTALADOR UNICO Gestao BIM: os tres plugins (Material Studio, DWG Export e GB Familias) para o Revit 2025,
  2026 e 2027, o catalogo de familias (banco com previews e dados de busca) e a busca por IA (gbfam.exe e modelos).
  So os arquivos .rfa das familias ficam de fora: o servidor entrega cada um quando o usuario insere a familia.
.DESCRIPTION
  Roda os tres scripts de pacote com -StageOnly, junta as pastas em dist\GestaoBIM-<versao> e gera:
    dist\GestaoBIM-<versao>.zip        (com Instalar.bat)
    dist\GestaoBIM-Setup-<versao>.exe  (build/SetupStub.cs: confere o SHA-256, recusa com o Revit aberto, /quiet)
  Os tres plugins tem a MESMA versao (o <Version> dos tres .csproj) e a release do GitHub usa a tag v<versao>.
.NOTES
  Precisa do gbfam.exe compilado (cargo build --release em tools/gbfam), dos modelos de IA e da pasta do catalogo gerado por 'gbfam export-catalog' (-CatalogDir ou GBFAM_CATALOG_DIR); os parametros sao repassados ao package-familias.ps1.
#>
param(
    [string]$Configuration = 'Release',
    [string]$CatalogDir = $env:GBFAM_CATALOG_DIR
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$root = Split-Path $PSScriptRoot

# Uma versao so: os tres plugins tem de concordar.
$versions = @{}
foreach ($p in 'GBMaterialStudio', 'GBDwgExport', 'GBFamilias') {
    [xml]$xml = Get-Content (Join-Path $root "src\$p\$p.csproj")
    $versions[$p] = [string]($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}
$version = $versions['GBFamilias']
if (@($versions.Values | Sort-Object -Unique).Count -ne 1) {
    throw "Os tres plugins precisam da mesma versao (instalador unico): $(($versions.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join ', ')"
}

# 1) cada plugin monta a sua pasta (compila os tres anos)
& (Join-Path $PSScriptRoot 'package.ps1') -Configuration $Configuration -StageOnly | Out-Host
& (Join-Path $PSScriptRoot 'package-dwg.ps1') -Configuration $Configuration -StageOnly | Out-Host
& (Join-Path $PSScriptRoot 'package-familias.ps1') -Configuration $Configuration -CatalogDir $CatalogDir -StageOnly | Out-Host

# 2) junta as tres pastas numa so (o Install.ps1 instala todo .addin que estiver no pacote)
$name = "GestaoBIM-$version"
$stage = Join-Path $root "dist\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
foreach ($p in 'GBMaterialStudio', 'GBDwgExport', 'GBFamilias') {
    $part = Join-Path $root "dist\$p-$version"
    if (-not (Test-Path (Join-Path $part "$p.addin"))) { throw "A pasta do $p nao foi montada: $part" }
    Get-ChildItem $part -Force | Where-Object { $_.Name -ne 'LEIA-ME.txt' } | ForEach-Object { Copy-Item $_.FullName $stage -Recurse -Force }
    Remove-Item $part -Recurse -Force
}
foreach ($required in 'GBMaterialStudio.addin', 'GBDwgExport.addin', 'GBFamilias.addin', 'Install.ps1', 'catalog\catalog.sqlite', 'catalog\catalog.version',
                      'tools\gbfam\gbfam.exe', 'tools\cadcraft', '2025\GBFamilias\GBFamilias.dll', '2027\GBMaterialStudio\GBMaterialStudio.dll', '2026\GBDwgExport\GBDwgExport.dll') {
    if (-not (Test-Path (Join-Path $stage $required))) { throw "Falta no pacote: $required" }
}
if (@(Get-ChildItem $stage -Recurse -Filter *.rfa).Count -gt 0) { throw 'O pacote nao deve levar arquivos .rfa (o servidor entrega as familias).' }

$catalogVersion = (Get-Content (Join-Path $stage 'catalog\catalog.version') -Raw).Trim()
$readme = @"
Gestao BIM $version - plugins para o Revit 2025, 2026 e 2027 (Windows)

O QUE VEM NESTE INSTALADOR
  - GB Material Studio: materiais (editar, conta-gotas, favoritos, biblioteca portatil, organizar).
  - GB DWG Export: exporta folhas e vistas para DWG/DXF, juntando as folhas num arquivo so, sem AutoCAD.
  - GB Familias: biblioteca de familias com busca por texto e por IA (aceita imagem), filtros e favoritos.
    Vem com o CATALOGO (versao ${catalogVersion}: previews, descricoes e dados de busca) e a busca por IA, que roda no
    seu computador. Os arquivos das familias (.rfa) NAO vem aqui: cada um e baixado do servidor quando voce o insere.
  Tudo aparece na aba "Gestao BIM" do Revit. O botao "Sobre" (painel Geral) mostra a versao e avisa de versao nova.

INSTALAR
  1. Feche o Revit.
  2. De dois cliques em GestaoBIM-Setup-$version.exe (ou, neste .zip, em Instalar.bat).
     Instala em todos os Revit 2025, 2026 e 2027 encontrados. Nao precisa de administrador.
  3. Abra o Revit. Na primeira vez ele pergunta se confia nos complementos: escolha "Sempre carregar".
  4. GB Familias, na primeira vez que inserir uma familia: escolha a PASTA DAS FAMILIAS (local ou de rede) e entre
     com a sua conta (o administrador cria as contas). Se voce ja tem familias numa pasta, escolha essa pasta: as que
     ja estao la sao reconhecidas e nao sao baixadas de novo.

DESINSTALAR
  Dois cliques em Desinstalar.bat. Seus favoritos e configuracoes continuam em %APPDATA%\GestaoBIM.

BUSCA POR IA (GB Familias)
  Roda no seu computador (CPU), sem enviar nada para fora: gbfam.exe e tres modelos ONNX em
  %APPDATA%\GestaoBIM\Tools\gbfam\models - CLIP ViT-B/32 (huggingface.co/Qdrant/clip-ViT-B-32-vision e
  clip-ViT-B-32-text) e multilingual-e5-small (huggingface.co/intfloat/multilingual-e5-small). A licenca de cada
  modelo e a do repositorio de origem.

DWG EXPORT
  Usa o cadcraft-cli original (github.com/storytold/cadcraft), em %APPDATA%\GestaoBIM\Tools\cadcraft, com a licenca dele.

CODIGO-FONTE E LICENCA
  https://github.com/valber99/Gestaobim-plugins  -  AGPL-3.0 (arquivo LICENSE)
"@
[IO.File]::WriteAllText((Join-Path $stage 'LEIA-ME.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))

# 3) .zip e .exe
$zip = Join-Path $root "dist\$name.zip"
New-ZipWithChecksum -Stage $stage -Zip $zip
New-LargeSetupExe -Zip $zip -Exe (Join-Path $root "dist\GestaoBIM-Setup-$version.exe") -Slug 'gestaobim'
