<#
.SYNOPSIS
  Gera o INSTALADOR UNICO Gestao BIM: os cinco plugins (Material Studio, DWG Export, GB Familias, GB Cotas e GB Render)
  para o Revit 2025, 2026 e 2027, o catalogo de familias (banco com previews e dados de busca), a busca por IA (gbfam.exe
  e modelos) e o gateway local de IA do GB Cotas e do GB Render (CLIProxyAPI, conferido por SHA-256).
  So os arquivos .rfa das familias ficam de fora: o servidor entrega cada um quando o usuario insere a familia.
.DESCRIPTION
  Roda os quatro scripts de pacote com -StageOnly, junta as pastas em dist\GestaoBIM-<versao> e gera:
    dist\GestaoBIM-<versao>.zip        (com Instalar.bat)
    dist\GestaoBIM-Setup-<versao>.exe  (build/SetupStub.cs: confere o SHA-256, recusa com o Revit aberto, /quiet)
  Os cinco plugins tem a MESMA versao (o <Version> dos cinco .csproj) e a release do GitHub usa a tag v<versao>.
.NOTES
  Precisa do gbfam.exe compilado (cargo build --release em tools/gbfam), dos modelos de IA e da pasta do catalogo
  gerado por 'gbfam export-catalog' (informe -CatalogDir ou defina a variavel de ambiente GBFAM_CATALOG_DIR).
#>
param(
    [string]$Configuration = 'Release',
    [string]$CatalogDir = $env:GBFAM_CATALOG_DIR
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$root = Split-Path $PSScriptRoot

# Uma versao so: os cinco plugins tem de concordar.
$versions = @{}
foreach ($p in 'GBMaterialStudio', 'GBDwgExport', 'GBFamilias', 'GBCotas', 'GBRender') {
    [xml]$xml = Get-Content (Join-Path $root "src\$p\$p.csproj")
    $versions[$p] = [string]($xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1)
}
$version = $versions['GBFamilias']
if (@($versions.Values | Sort-Object -Unique).Count -ne 1) {
    throw "Os cinco plugins precisam da mesma versao (instalador unico): $(($versions.GetEnumerator() | ForEach-Object { "$($_.Key) $($_.Value)" }) -join ', ')"
}

# 1) cada plugin monta a sua pasta (compila os tres anos)
& (Join-Path $PSScriptRoot 'package.ps1') -Configuration $Configuration -StageOnly | Out-Host
& (Join-Path $PSScriptRoot 'package-dwg.ps1') -Configuration $Configuration -StageOnly | Out-Host
& (Join-Path $PSScriptRoot 'package-familias.ps1') -Configuration $Configuration -CatalogDir $CatalogDir -StageOnly | Out-Host
& (Join-Path $PSScriptRoot 'package-ia.ps1') -Configuration $Configuration | Out-Host

# 2) junta as quatro pastas numa so (o Install.ps1 instala todo .addin que estiver no pacote)
$name = "GestaoBIM-$version"
$stage = Join-Path $root "dist\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
foreach ($p in 'GBMaterialStudio', 'GBDwgExport', 'GBFamilias', 'GBIa') {
    $part = Join-Path $root "dist\$p-$version"
    $manifest = if ($p -eq 'GBIa') { 'GBCotas.addin' } else { "$p.addin" }
    if (-not (Test-Path (Join-Path $part $manifest))) { throw "A pasta do $p nao foi montada: $part" }
    Get-ChildItem $part -Force | Where-Object { $_.Name -ne 'LEIA-ME.txt' } | ForEach-Object { Copy-Item $_.FullName $stage -Recurse -Force }
    Remove-Item $part -Recurse -Force
}
$thirdParty = @"
Componentes de terceiros distribuidos SEM modificacao neste instalador

CLIProxyAPI $(Get-Content (Join-Path $root 'src\GBAi.Shared\GatewayHost.cs') | Select-String 'Version = "([^"]+)"' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Select-Object -First 1)
  https://github.com/router-for-me/CLIProxyAPI  -  licenca MIT (texto completo em tools\cliproxyapi\LICENSE-CLIProxyAPI.txt,
  instalado em %APPDATA%\GestaoBIM\Tools\cliproxyapi). Gateway local de IA do GB Cotas e do GB Render.

cadcraft-cli (GB DWG Export)
  https://github.com/storytold/cadcraft  -  MIT ou Apache-2.0 (avisos em tools\cadcraft).

Modelos ONNX da busca por IA (GB Familias)
  CLIP ViT-B/32 (huggingface.co/Qdrant/clip-ViT-B-32-vision e clip-ViT-B-32-text) e multilingual-e5-small
  (huggingface.co/intfloat/multilingual-e5-small), com a licenca do repositorio de origem.
"@
[IO.File]::WriteAllText((Join-Path $stage 'THIRD-PARTY.txt'), $thirdParty, (New-Object System.Text.UTF8Encoding($true)))

foreach ($required in 'GBMaterialStudio.addin', 'GBDwgExport.addin', 'GBFamilias.addin', 'Install.ps1', 'catalog\catalog.sqlite', 'catalog\catalog.version',
                      'tools\gbfam\gbfam.exe', 'tools\cadcraft', '2025\GBFamilias\GBFamilias.dll', '2027\GBMaterialStudio\GBMaterialStudio.dll', '2026\GBDwgExport\GBDwgExport.dll',
                      'GBCotas.addin', 'GBRender.addin', 'tools\cliproxyapi\cli-proxy-api.exe', 'tools\cliproxyapi\LICENSE-CLIProxyAPI.txt', 'THIRD-PARTY.txt',
                      '2025\GBCotas\GBCotas.dll', '2026\GBCotas\GBCotas.dll', '2027\GBCotas\GBCotas.dll', '2025\GBRender\GBRender.dll', '2026\GBRender\GBRender.dll', '2027\GBRender\GBRender.dll') {
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
  - GB Cotas: cota a planta aberta (paredes, aberturas e eixos) com cotas nativas do Revit, sem sobreposicao. Funciona
    so por regras; com IA, ela sugere quais cadeias criar e o plugin confere tudo antes de cotar.
  - GB Render: gera o render da vista 3D aberta com IA a partir de quatro camadas alinhadas (clay, cores por elemento,
    profundidade e fundo) e de um manifesto com o que esta na cena. Pede para editar so uma parte da imagem.
  Tudo aparece na aba "Gestao BIM" do Revit. O botao "Sobre" (painel Geral) mostra a versao e avisa de versao nova.

INSTALAR
  1. Feche o Revit.
  2. De dois cliques em GestaoBIM-Setup-$version.exe (ou, neste .zip, em Instalar.bat).
     Instala em todos os Revit 2025, 2026 e 2027 encontrados. Nao precisa de administrador.
  3. Abra o Revit. Na primeira vez ele pergunta se confia nos complementos: escolha "Sempre carregar".
  4. GB Familias, para baixar e inserir familias da biblioteca:
     - Cadastre-se primeiro em: https://cadastro.gestaobim.com (aguarde a aprovacao da conta).
     - Na primeira vez que inserir uma familia, escolha a PASTA DAS FAMILIAS (local ou de rede) e entre com a sua conta.
     - Se voce ja tem familias numa pasta, escolha essa pasta: as que ja estao la sao reconhecidas e nao sao baixadas de novo.

DESINSTALAR
  Dois cliques em Desinstalar.bat (no .zip ou em %APPDATA%\GestaoBIM\Desinstalar.bat). Seus favoritos e configuracoes continuam em %APPDATA%\GestaoBIM.

BUSCA POR IA (GB Familias)
  Roda no seu computador (CPU), sem enviar nada para fora: gbfam.exe e tres modelos ONNX em
  %APPDATA%\GestaoBIM\Tools\gbfam\models - CLIP ViT-B/32 (huggingface.co/Qdrant/clip-ViT-B-32-vision e
  clip-ViT-B-32-text) e multilingual-e5-small (huggingface.co/intfloat/multilingual-e5-small). A licenca de cada
  modelo e a do repositorio de origem.

IA DO GB COTAS E DO GB RENDER (opcional, painel IA > Configurar IA)
  Usa um gateway local, o CLIProxyAPI original (github.com/router-for-me/CLIProxyAPI, licenca MIT), em
  %APPDATA%\GestaoBIM\Tools\cliproxyapi. Ele so escuta neste computador (127.0.0.1), com uma senha nova a cada sessao,
  e fecha junto com o Revit. Voce escolhe como pagar a IA: com a sua chave de API (Gemini, Claude ou OpenAI; guardada
  cifrada no Windows) ou entrando com a sua assinatura (Claude Code, ChatGPT Codex ou Antigravity). Entrar com a
  assinatura pode ir contra os termos de uso do provedor: o plugin avisa e so continua se voce aceitar.
  O que vai para a IA: o resumo da planta (GB Cotas) ou as imagens e a lista de objetos da vista (GB Render). Nada e
  enviado sem voce clicar.

DWG EXPORT
  Usa o cadcraft-cli original (github.com/storytold/cadcraft), em %APPDATA%\GestaoBIM\Tools\cadcraft, com a licenca dele.

CODIGO-FONTE E LICENCA
  https://github.com/valber99/Gestaobim-plugins  -  AGPL-3.0 (arquivo LICENSE). Terceiros: THIRD-PARTY.txt
"@
[IO.File]::WriteAllText((Join-Path $stage 'LEIA-ME.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))

# 3) .zip e .exe
$zip = Join-Path $root "dist\$name.zip"
New-ZipWithChecksum -Stage $stage -Zip $zip
New-LargeSetupExe -Zip $zip -Exe (Join-Path $root "dist\GestaoBIM-Setup-$version.exe") -Slug 'gestaobim'
