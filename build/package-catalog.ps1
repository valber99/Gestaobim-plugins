<#
.SYNOPSIS
  Gera o pacote SO DO CATALOGO do GB Familias (GBFamilias-Catalogo-<versao>.zip, alguns MB): para atualizar o banco de
  familias de quem ja tem o plugin instalado, sem reinstalar o plugin nem baixar de novo os ~1 GB do motor de IA.
  O Instalar.bat so troca o catalogo se a versao do pacote for mais nova que a instalada.
.NOTES
  O catalogo vem da pasta gerada por 'gbfam export-catalog' (-CatalogDir ou GBFAM_CATALOG_DIR).
#>
param(
    [string]$CatalogDir = $env:GBFAM_CATALOG_DIR
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Split-Path $PSScriptRoot
$manifestFile = Join-Path $CatalogDir 'manifest.json'
if (-not (Test-Path $manifestFile)) { throw "Catalogo nao encontrado em ${CatalogDir}. Gere com: 'gbfam export-catalog'." }
$manifest = Get-Content $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
$snapshot = Join-Path $CatalogDir $manifest.snapshot.file
if (-not (Test-Path $snapshot)) { throw "Falta $($manifest.snapshot.file) em $CatalogDir." }
if ((Get-FileHash $snapshot -Algorithm SHA256).Hash.ToLowerInvariant() -ne $manifest.snapshot.sha256) {
    throw 'O snapshot do catalogo nao confere com o manifesto (hash diferente). Gere o catalogo de novo.'
}

$name = "GBFamilias-Catalogo-$($manifest.version)"
$stage = Join-Path $root "dist\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'catalog') | Out-Null

Expand-GzipFile -Path $snapshot -Destination (Join-Path $stage 'catalog\catalog.sqlite')
[IO.File]::WriteAllText((Join-Path $stage 'catalog\catalog.version'), [string]$manifest.version)
Copy-Item (Join-Path $root 'installer\Install.ps1') $stage
Copy-Item (Join-Path $root 'installer\Instalar.bat') $stage

$readme = @"
GB Familias - Catalogo versao $($manifest.version) ($($manifest.families) familias)

Atualiza so o CATALOGO (banco com previews e dados de busca) de quem ja tem o plugin GB Familias instalado.
Nao reinstala o plugin nem o motor de IA.

  1. Feche o Revit.
  2. De dois cliques em Instalar.bat.
  3. Abra o Revit: a biblioteca ja mostra as familias novas.

Se voce ja tem este catalogo (ou um mais novo), o instalador mantem o seu e avisa.
Suas familias baixadas, favoritos e colecoes nao sao tocados.
"@
[IO.File]::WriteAllText((Join-Path $stage 'LEIA-ME.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))

New-ZipWithChecksum -Stage $stage -Zip (Join-Path $root "dist\$name.zip")
