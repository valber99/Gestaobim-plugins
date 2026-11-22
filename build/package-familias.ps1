<#
.SYNOPSIS
  Compila o GB Familias para o Revit 2025, 2026 e 2027 e gera o pacote do plugin (.zip e o instalador GBFamilias-Setup-<versao>.exe).
  O pacote leva o gbfam.exe (motor de busca por IA) e os modelos ONNX (~1 GB): a busca por IA funciona offline.
  O CATALOGO (banco com previews e dados de busca) vai no pacote; os arquivos .rfa NAO: o servidor entrega cada um
  quando voce insere a familia (o administrador gera o catalogo com 'gbfam export-catalog').
.NOTES
  O gbfam.exe e os modelos NAO estao no repositorio (tools/gbfam/ compila o gbfam; os modelos ficam fora do git): informe os caminhos nos parametros.
#>
param(
    [string]$Configuration = 'Release',
    [int[]]$Years = @(2025, 2026, 2027),
    [string]$GbfamExe = (Join-Path (Split-Path $PSScriptRoot) 'tools\gbfam\target\release\gbfam.exe'),
    [string]$Models = (Join-Path (Split-Path $PSScriptRoot) 'tools\gbfam\.models'),
    # O IExpress trava com este payload (~0,7 GB), entao o .exe usa o instalador proprio (build/SetupStub.cs, o .zip
    # colado no fim, conferido por SHA-256). -NoExe gera so o .zip.
    [switch]$NoExe,
    # O catalogo (banco) vai dentro do instalador. Vem da pasta gerada por 'gbfam export-catalog'.
    [string]$CatalogDir = $env:GBFAM_CATALOG_DIR
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')

$root = Split-Path $PSScriptRoot
$project = Join-Path $root 'src\GBFamilias\GBFamilias.csproj'
[xml]$xml = Get-Content $project
$version = ($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'Version nao encontrada no csproj.' }

if (-not (Test-Path $GbfamExe)) { throw "gbfam.exe nao encontrado: $GbfamExe (compile com 'cargo build --release' em tools/gbfam)" }
foreach ($required in 'models--intfloat--multilingual-e5-small', 'models--Qdrant--clip-ViT-B-32-text', 'models--Qdrant--clip-ViT-B-32-vision') {
    if (-not (Test-Path (Join-Path $Models $required))) { throw "Modelo ausente em ${Models}: $required (rode 'gbfam embed' uma vez para baixa-los)" }
}

$name = "GBFamilias-$version"
$stage = Join-Path $root "dist\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

# 1) O motor de busca por IA: uma copia so, em %APPDATA%\GestaoBIM\Tools\gbfam, para todos os anos do Revit.
$toolsStage = Join-Path $stage 'tools\gbfam'
New-Item -ItemType Directory -Force $toolsStage | Out-Null
Copy-Item $GbfamExe $toolsStage
Copy-Item $Models (Join-Path $toolsStage 'models') -Recurse
$cliVersion = & (Join-Path $toolsStage 'gbfam.exe') --version 2>$null
Write-Host "Motor de busca: gbfam $cliVersion ($([math]::Round((Get-ChildItem $toolsStage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)) MB)"

# 2) O add-in, uma DLL por versao do Revit (mais as DLLs do SQLite, que vao ao lado).
foreach ($year in $Years) {
    Write-Host "Compilando para o Revit $year..."
    dotnet build $project -c $Configuration "-p:RevitVersion=$year" --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar para o Revit $year" }
    $out = Join-Path $root "src\GBFamilias\bin\$Configuration\$year"
    $payload = Join-Path $stage "$year\GBFamilias"
    New-Item -ItemType Directory -Force $payload | Out-Null
    foreach ($dll in 'GBFamilias.dll', 'Microsoft.Data.Sqlite.dll', 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.provider.winsqlite3.dll') {
        Copy-Item (Join-Path $out $dll) $payload
    }
}

foreach ($file in 'Install.ps1', 'Uninstall.ps1', 'Instalar.bat', 'Desinstalar.bat') {
    Copy-Item (Join-Path $root "installer\$file") $stage
}
# 2) O catalogo (banco com previews, dados de busca e vetores): vai DENTRO do instalador, o servidor so entrega os .rfa.
#    Sai do `gbfam export-catalog` (a pasta catalog-out do repositorio do servidor): snapshot .gz + manifesto com o hash.
$manifestFile = Join-Path $CatalogDir 'manifest.json'
if (-not (Test-Path $manifestFile)) {
    throw "Catalogo nao encontrado em ${CatalogDir}. Gere com: 'gbfam export-catalog', ou informe -CatalogDir."
}
$manifest = Get-Content $manifestFile -Raw -Encoding UTF8 | ConvertFrom-Json
$snapshotFile = Join-Path $CatalogDir $manifest.snapshot.file
if (-not (Test-Path $snapshotFile)) { throw "Falta $($manifest.snapshot.file) em $CatalogDir." }
$actualHash = (Get-FileHash $snapshotFile -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $manifest.snapshot.sha256) { throw "O snapshot do catalogo nao confere com o manifesto (hash diferente). Gere o catalogo de novo." }
$catalogStage = Join-Path $stage 'catalog'
New-Item -ItemType Directory -Force $catalogStage | Out-Null
Expand-GzipFile -Path $snapshotFile -Destination (Join-Path $catalogStage 'catalog.sqlite')
[IO.File]::WriteAllText((Join-Path $catalogStage 'catalog.version'), [string]$manifest.version)
$catalogMb = [math]::Round((Get-Item (Join-Path $catalogStage 'catalog.sqlite')).Length / 1MB, 1)
Write-Host "Catalogo versao $($manifest.version): $($manifest.families) familias ($catalogMb MB)"

Copy-Item (Join-Path $root 'installer\GBFamilias.addin') $stage
Copy-Item (Join-Path $root 'LICENSE') $stage

$readme = @"
GB Familias $version
Biblioteca de familias do Revit com busca por texto e por imagem, filtros, categorias, favoritos e colecoes (Gestao BIM)

Nao precisa de pyRevit nem de Python. A busca funciona sem internet; baixar familias precisa do servidor.

INSTALAR
  1. Feche o Revit.
  2. De dois cliques em GBFamilias-Setup-$version.exe (ou, no .zip, em Instalar.bat). Ele instala o plugin, o motor de busca por IA e o CATALOGO de familias
     (previews e dados de busca), tudo no seu computador.
  3. Abra o Revit: o painel "Familias" aparece na aba "Gestao BIM". Clique em "Biblioteca": a busca ja funciona.
  4. Na primeira vez que inserir uma familia: escolha a PASTA DAS FAMILIAS BAIXADAS (obrigatoria; local ou de rede)
     e entre com a sua conta (abre o navegador; o administrador cria as contas).
  Na primeira vez o Revit pergunta se confia no add-in: escolha "Sempre carregar".
  Cada arquivo de familia e baixado do servidor quando voce o insere; o que ja foi baixado funciona sem internet.
  Catalogo novo: o administrador entrega um pacote pequeno "GBFamilias-Catalogo" (Instalar.bat atualiza so o catalogo).

DESINSTALAR
  Dois cliques em Desinstalar.bat. Seus favoritos e colecoes continuam em %APPDATA%\GestaoBIM\Familias.

REVIT SUPORTADO
  2025, 2026 e 2027 (Windows).

BUSCA POR IA
  Roda no seu computador (CPU), sem enviar nada para fora. Usa o gbfam.exe e tres modelos ONNX, instalados em
  %APPDATA%\GestaoBIM\Tools\gbfam\models:
    - CLIP ViT-B/32, imagem e texto (huggingface.co/Qdrant/clip-ViT-B-32-vision e clip-ViT-B-32-text)
    - multilingual-e5-small (huggingface.co/intfloat/multilingual-e5-small)
  A licenca de cada modelo e a do respectivo repositorio de origem: confira antes de redistribuir o pacote.

CODIGO-FONTE E LICENCA
  https://github.com/valber99/Gestaobim-plugins  -  AGPL-3.0 (arquivo LICENSE)
"@
[IO.File]::WriteAllText((Join-Path $stage 'LEIA-ME.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))

$zip = Join-Path $root "dist\$name.zip"
New-ZipWithChecksum -Stage $stage -Zip $zip
if (-not $NoExe) {
    New-LargeSetupExe -Zip $zip -Exe (Join-Path $root "dist\GBFamilias-Setup-$version.exe") -Slug 'gb-familias'
}
