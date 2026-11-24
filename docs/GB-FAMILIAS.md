# GB Famílias

Biblioteca de famílias do Revit com **busca por texto e por imagem**, filtros, categorias, favoritos e coleções.
Aparece como o painel **Famílias** na aba **Gestão BIM**, junto dos outros plugins.

Este documento tem o guia do usuário, a arquitetura do plugin e como empacotar. Desde a 0.4.0 o GB Famílias sai no **instalador
único Gestão BIM** (`GestaoBIM-Setup-<versão>.exe`, tag `v<versão>`), junto dos outros plugins.

## Guia do usuário

### Como funciona (versão 0.2)

O **catálogo** (nomes, previews, descrições e os dados da busca por IA) **vem dentro do instalador do plugin** e fica no
seu computador, então **buscar e filtrar funciona sem internet, já na primeira abertura**. O **servidor só entrega os
arquivos `.rfa`**: cada um é baixado só quando você insere a família, um de cada vez, e fica numa pasta que você escolhe.
O que já foi baixado funciona offline.

### Instalar

1. Feche o Revit e dê dois cliques em `GestaoBIM-Setup-<versão>.exe` (ou, no `.zip`, em `Instalar.bat`). Ele instala os
   três plugins nos Revit 2025, 2026 e 2027, o motor de busca por IA (`gbfam.exe` e os modelos, ~1 GB) em
   `%APPDATA%\GestaoBIM\Tools` e o **catálogo** em `%LOCALAPPDATA%\GestaoBIM\Familias` (só troca o que já existe se o
   do pacote for mais novo). Nada disso precisa ser configurado.
2. Abra o Revit: painel **Famílias**, botão **Biblioteca**. A busca já funciona.
3. Na primeira vez que inserir uma família:
   - escolha a **pasta das famílias** (obrigatória; pode ser local ou de rede). Se você **já tem famílias** numa pasta
     (ou a biblioteca inteira), escolha essa pasta: as que já estão lá aparecem como **✓ baixada** e não são baixadas
     de novo;
   - faça seu cadastro em **[https://cadastro.valbermotta.org](https://cadastro.valbermotta.org)** (o acesso
     passa por aprovação do administrador) e depois clique em **Entrar** no plugin (abre o navegador para login com o
     e-mail e a senha cadastrados).

### Buscar

- **Caixa de busca**: digite nome, material, cor, ambiente ou tipo. Acentos e maiúsculas não importam, e palavras
  como "de" e "com" são ignoradas. O resultado aparece enquanto você digita.
- **Busca por IA** (ligada por padrão): além das palavras, entende o sentido ("lavatório" acha pias) e refina a ordem
  dos resultados alguns instantes depois. O indicador no canto mostra se está pronta. Funciona sem internet.
- **Buscar por imagem…**: escolha uma foto, print ou desenho e veja as famílias mais parecidas.
- **Ver parecidas** (painel de detalhes): usa o preview da família selecionada como imagem de busca.
- Clique numa etiqueta (tag) do painel de detalhes para buscar por ela.

### Filtrar e organizar

- **Categorias**: árvore com subcategorias e contagens. A contagem de cada item já considera os outros filtros e
  a busca, então você nunca marca um filtro que dê zero resultado sem perceber.
- **Pasta de origem, Ambiente, Material, Cor, Estilo, Versão do Revit, Categoria no Revit**: dentro de um grupo
  vale "ou"; entre grupos vale "e". Listas longas têm uma caixa para filtrar.
- **Somente com preview**, **Somente baixadas** (as que já estão no seu computador), **Ordem** (relevância, nome,
  categoria) e **Limpar filtros**.
- **Coleções**: *Todas*, *★ Favoritas*, *Recentes* (as últimas inseridas) e as suas, criadas com **Nova coleção**.
  Use **Coleção ▾** no painel de detalhes para pôr a família numa ou mais coleções. A estrela do cartão favorita.
- A marca verde **✓ baixada** no cartão e a frase no painel de detalhes dizem se a família já está no seu computador.

### Inserir no projeto

Selecione a família e clique em **Inserir no projeto** (ou **Baixar e inserir**, se ainda não foi baixada; também vale
dar dois cliques no cartão). O plugin baixa o arquivo (barra de andamento e **Cancelar** na barra de status), confere
a integridade (SHA-256) e só então o Revit carrega a família e inicia o posicionamento; a janela se esconde e volta
quando você termina (Esc encerra). Detalhes:

- **Um download por vez** por usuário. Se o servidor pedir para esperar (limite de downloads), o plugin espera e
  continua sozinho, mostrando o tempo.
- Se a família já existe no projeto, ela é reaproveitada (não é recarregada por cima).
- Famílias salvas em versão **mais antiga** do Revit são atualizadas ao carregar (o arquivo baixado não muda, e o
  painel de detalhes avisa antes). Famílias de versão **mais nova** que a do seu Revit não abrem, e o plugin diz isso.
- Perfis, anotações e documentos de família são carregados mas não posicionados; o plugin avisa e você acha a
  família no Navegador de Projeto.
- Carregar a família e posicionar são operações separadas do Revit: para desfazer tudo, use Ctrl+Z mais de uma vez.

### Sem internet

A busca, os filtros, a busca por IA, os favoritos e as coleções funcionam normalmente. Só **inserir uma família que
ainda não foi baixada** precisa do servidor. Use o filtro **Somente baixadas** para ver o que está disponível offline.

### Atualizar o catálogo

O catálogo novo chega por **instalador**: o administrador entrega o pacote pequeno `GBFamilias-Catalogo-<versão>.zip`
(alguns MB; o `Instalar.bat` atualiza só o catálogo, sem reinstalar o plugin nem os ~1 GB do motor de IA) ou um
`GBFamilias-<versão>.zip` completo. Feche o Revit antes. Se você já tem aquele catálogo ou um mais novo, o instalador
mantém o seu. Suas famílias baixadas, favoritos e coleções não são tocados.

**Atualização pelo servidor (opcional, desligada).** Em `settings.json`, `"catalog_sync": true` faz o plugin procurar
catálogo novo no servidor (no máximo a cada 6 horas), baixar só o que mudou (*delta*) e perguntar se quer aplicar, e
mostra o botão **Atualizar catálogo**. O catálogo do servidor é **assinado**: assinatura que não confere, arquivo
adulterado ou versão mais antiga que a instalada são recusados. Só vale a pena quando o acervo crescer muito.

### Configurações

**Famílias > Configurações** tem só três coisas; o resto é padrão (catálogo e busca por IA instalados, famílias vindas
do servidor):

- **Pasta das famílias do Revit**: onde as famílias baixadas ficam. Pode ser uma pasta onde você **já tem famílias**:
  o plugin as reconhece, primeiro pelo mesmo caminho da biblioteca e, se você organizou do seu jeito, pelo **nome do
  arquivo e o tamanho** em qualquer subpasta. Ao inserir, confere o SHA-256; se o arquivo não for o mesmo do catálogo,
  baixa o certo do servidor (na estrutura de pastas da biblioteca).
- **Espaço usado**: **Sem limite** (padrão) ou **Limitar a N GB**. Com limite, as famílias **baixadas pelo plugin** menos
  usadas são apagadas (podem ser baixadas de novo); as que já estavam na pasta e as pastas de rede **nunca** são apagadas.
- **Conta**: **Entrar** / **Sair**.

Quem usava a 0.1 com a biblioteca numa pasta ("pasta local") passa a usar o servidor sozinho: a pasta da biblioteca
vira a pasta das famílias, então tudo o que já tinha continua disponível, sem baixar de novo.

Ficam em `%APPDATA%\GestaoBIM\Familias\settings.json`. Favoritos, coleções e recentes ficam em `user-data.json` na
mesma pasta: por usuário. O catálogo e o login ficam em `%LOCALAPPDATA%\GestaoBIM\Familias` (o login é protegido
pelo Windows e só funciona para você, neste computador).

**Pasta de rede compartilhada.** Vários computadores podem usar a mesma pasta de downloads: o que um baixa serve a
todos. Cada computador grava num arquivo temporário próprio e o arquivo final nunca é sobrescrito por outro, então
dois downloads simultâneos do mesmo arquivo não se atrapalham.

### Problemas comuns

| Sintoma | O que fazer |
|---|---|
| "O catálogo da biblioteca não está instalado neste computador" | Rode de novo o instalador Gestão BIM (o catálogo vai junto) ou o pacote `GBFamilias-Catalogo`. |
| Pede a pasta das famílias ao inserir | É obrigatória para baixar (a busca funciona sem ela): escolha uma pasta onde você tenha permissão de gravar (o caminho deve ter até 150 caracteres). |
| Tenho a família na pasta, mas ela não aparece como baixada | O plugin reconhece pelo caminho da biblioteca ou pelo nome do arquivo **e** o tamanho. Um arquivo renomeado ou de outra versão (tamanho diferente) não é reconhecido: o plugin baixa o do catálogo. |
| "Sua conta não tem permissão" | A conta existe, mas o acesso ainda não foi liberado (ou foi revogado). Se você acabou de se cadastrar em [cadastro.valbermotta.org](https://cadastro.valbermotta.org), aguarde a aprovação do administrador. |
| "Entre com a sua conta" / "A sessão expirou" | Clique em **Entrar** (barra de status ou Configurações). |
| "Sem conexão com o servidor" | As famílias já baixadas e a busca continuam funcionando. Tente de novo quando a internet voltar. |
| "O servidor limitou os downloads" | Há um limite por minuto e por dia. O plugin espera e tenta de novo; se passar da cota do dia, volte amanhã. |
| "A assinatura do catálogo NÃO confere" | O catálogo foi recusado por segurança. Avise o administrador; o plugin segue com o catálogo antigo. |
| "IA indisponível" no canto da janela | Passe o mouse para ver o motivo. Em geral falta o `gbfam.exe`: rode o instalador Gestão BIM de novo. A busca por texto e os filtros continuam funcionando. |
| Uma família sem preview | ~1% da biblioteca não traz miniatura; ela aparece como "sem preview" e continua pesquisável pelo nome e pela descrição. |

## Arquitetura

```
src/GBFamilias.Core          regras puras (sem Revit): índice, filtros, busca, favoritos, caminhos, cliente de IA
src/GBFamilias.Core/Server   login (OAuth PKCE), cliente do servidor, catálogo (assinatura, delta), downloads
src/GBFamilias               o add-in: comandos, janela WPF e a camada do Revit (inserir, despachar eventos, DPAPI)
tests/GBFamilias.Core.Tests  168 testes xUnit
tools/gbfam                  o motor em Rust: ingestão, IA generativa, embeddings, busca, catálogo e o servidor local (gbfam)
installer, build             manifesto .addin, instaladores e empacotamento
```

O **servidor de arquivos e autenticação** é mantido separadamente deste repositório público. O plugin só conhece o
contrato HTTP público abaixo.

Mesmo padrão dos outros plugins: um código-fonte e uma compilação por ano do Revit (`-p:RevitVersion=2025|2026|2027`),
o Core compilado dentro da DLL do add-in, a aba **Gestão BIM** compartilhada, o `Install.ps1` genérico, o botão
**Sobre** único com aviso de atualização (tag `v<versão>`, a do instalador único; `src/GBShared/AboutHub.cs`, ver abaixo) e ferramentas em
`%APPDATA%\GestaoBIM\Tools`.

### Contrato público da API

| Rota | O que entrega |
|---|---|
| `GET /v1/rfa/<sha256>` | o arquivo da família, com `Range` (retomada). **É o que o plugin usa.** |
| `GET /v1/catalog/manifest.json` + `manifest.sig` | (opcional, `catalog_sync`) versão, hashes e deltas do catálogo, **assinado com Ed25519** |
| `GET /v1/catalog/<arquivo>` | (opcional) snapshot (`catalog-<v>.sqlite.gz`) ou delta (`delta-<a>-<b>.json.gz`) |

Todas exigem `Authorization: Bearer <token>` (OIDC/OAuth 2.0 + PKCE) e o papel `revit:user`. O pedido é **só pelo
SHA-256**: o servidor não aceita caminho nem nome de arquivo, impedindo pedidos fora do catálogo. (O caminho relativo de
cada família viaja no catálogo para organizar as pastas no computador do usuário.)

### Decisões

**Um só botão "Sobre" para os plugins.** Cada plugin é uma DLL separada, então `src/GBShared/AboutHub.cs` é compilado
dentro de cada uma e as cópias se coordenam por dados do `AppDomain` (só tipos da biblioteca padrão, que têm a mesma
identidade em todas as DLLs): o primeiro plugin a iniciar cria o painel **Geral** com o botão; os outros só se
registram. Ao clicar, uma janela mostra todos os plugins instalados (versão, aviso de versão nova e diagnóstico de cada
um); a bolinha vermelha acende se qualquer um tiver versão nova. Testado nos Revit 2025, 2026 e 2027.

**Login: OAuth 2.0 Authorization Code + PKCE, sem segredo no plugin.** O plugin abre o navegador do sistema e recebe
o retorno num servidor mínimo em `127.0.0.1` (uma das portas 53682 a 53684, só `TcpListener`, sem reserva de URL nem
administrador). O cliente OIDC é público (sem segredo embutido), o `state` é conferido, e o `access_token` é renovado
sozinho (com o `refresh_token`, uma única renovação mesmo com várias chamadas ao mesmo tempo). Os tokens ficam no disco
protegidos pela **DPAPI do Windows** (chamada direta a `crypt32`, sem pacote extra dentro do Revit). Sem internet a
renovação **não** apaga a sessão; só uma recusa do servidor (`invalid_grant`) apaga.

**Catálogo: no instalador; a atualização pelo servidor (opcional) é assinada, em delta e nunca troca o banco em uso.**
O instalador copia o catálogo para `%LOCALAPPDATA%` (só se for mais novo que o instalado). Se `catalog_sync` estiver
ligado, o administrador publica `manifest.json` assinado
com **Ed25519** (a chave privada fica só com ele; a pública vai dentro do plugin, que a confere com uma implementação
gerenciada de Ed25519, já que o .NET não traz a assinatura). O plugin baixa, confere o hash de cada arquivo contra o
manifesto assinado, aplica os deltas **numa cópia** (comandos parametrizados: o delta é dado, nunca SQL), valida
(`quick_check`, número de famílias e versão) e só então a troca acontece, com o banco fechado, na próxima abertura ou
quando você aceita. Versão menor que a instalada é recusada (proteção contra *rollback*). Se a cadeia de deltas tem
buraco, o plugin cai no snapshot.

**Download: um de cada vez, conferido e à prova de rede.** Fila de um, `Range` para retomar, espera o `Retry-After`
do servidor quando há limite, grava em `.part` e confere o **SHA-256 do catálogo local** (não de um cabeçalho do
mesmo servidor). Hash diferente = tenta de novo uma vez e então falha, sem deixar lixo. O cache local é limpo pelo
mais antigo quando passa do limite (nunca em pasta de rede, nunca o arquivo que acabou de baixar).

**Metadados em memória, não consultas SQL.** `LibraryDb.LoadIndex` lê tudo uma vez e `LibraryIndex` filtra, conta
facetas e busca em C#. Medido com 25 mil famílias: carrega em ~2 s (115 MB), consultas de texto levam 13 a 24 ms e as
contagens de todas as facetas 18 ms (`CountsAll`, uma passada em vez de uma por faceta).

**Filtros com facetas "estilo e-commerce".** A contagem de um grupo ignora a própria seleção do grupo, mas respeita
os outros grupos e o texto. Categoria e subcategoria formam um grupo só. As variantes que a IA escreve ("Veículo",
"Veículos", "Veiculo") viram uma chave só (`CategoryNormalizer`); o rótulo é a forma mais frequente.

**Busca híbrida.** O texto aparece na hora, pontuado por onde a palavra casou (nome > etiqueta > categoria >
material/cor/ambiente > descrição). Em paralelo a busca por IA devolve um ranking semântico dentro do conjunto que
passa nos filtros, e os dois são fundidos por *reciprocal rank fusion* (`Ranker.Merge`). Respostas atrasadas são
descartadas por número de versão.

**SQLite sem DLL nativa e sem *pool*.** O plugin lê o banco com `Microsoft.Data.Sqlite.Core` e o provedor `winsqlite3`
do SQLitePCLRaw (o `winsqlite3.dll` do Windows). O *pool* de conexões fica desligado de propósito: ele manteria o
arquivo aberto depois do `Dispose` e o Windows não deixaria trocar o catálogo.

**A IA roda fora do Revit, em um processo filho.** O plugin sobe `gbfam serve`: HTTP só em `127.0.0.1`, porta livre,
**token aleatório** por sessão. Os modelos (CLIP e e5, ~1 GB) carregam uma vez (~5 s) e a busca responde em ~0,4 s
mesmo com 25 mil famílias. Se o Revit morrer, o `stdin` fecha e o servidor encerra sozinho.

**Janela que não bloqueia o Revit.** Botões da janela enfileiram o trabalho num `ExternalEvent` (`RevitDispatcher`).
Ao inserir, a janela se esconde para o posicionamento receber o foco e volta no fim.

**Caminhos seguros.** O catálogo guarda o caminho **relativo** de cada família; `ResolveFamilyPath` recusa caminho
absoluto, letra de unidade e `..`, então um catálogo adulterado não faz o plugin gravar nem carregar fora da pasta.

**Dados do usuário separados da biblioteca.** Favoritos, coleções e recentes usam o **hash do conteúdo** da família
como chave: sobrevivem a trocas de catálogo. Cada alteração relê o arquivo dentro de um mutex nomeado; a gravação é
atômica; um arquivo corrompido é guardado ao lado (`.corrompido-<data>`) e nunca sobrescrito.

### O motor `gbfam` (Rust)

Fica em `tools/gbfam/` e produz tudo que a biblioteca contém (o banco e os modelos ficam fora do git):

- `ingest`: varre as `.rfa`, lê o preview (PNG, ou JPEG nos Revit antigos), versão e categoria dos streams OLE2, e
  deduplica por SHA-256.
- `enrich`: descrição, categoria, etiquetas, materiais, cores, estilo e ambientes gerados pelo **Claude Haiku 5.5** a
  partir da miniatura, do nome e dos metadados. Mostra o uso e o custo (medido: ~US$ 0,0005 por família).
- `embed`: vetores CLIP (imagem e texto) e e5 (descrição) em CPU.
- `gen-key`, `export-catalog`, `verify-catalog`, `check-store`: o catálogo versionado e assinado (abaixo).
- `search`, `serve`, `export-library`, `missing`, `import-previews`, `stats`, `inspect`.

## Escala (medido com 25 mil famílias sintéticas)

| | 3.368 famílias (real) | 25.000 (sintético) |
|---|---|---|
| Catálogo exportado (`.gz`) | 32 MB | 248 MB (previews 124 MB, vetores 134 MB, textos 37 MB) |
| Abrir a biblioteca (carregar o índice) | 0,3 s | 2,0 s (115 MB de memória) |
| Busca de texto no plugin | 3 ms | 13 a 24 ms |
| Contagem de todas as facetas | 2,5 ms | 18 ms |
| Busca por IA | ~0,1 a 0,4 s | ~0,42 s (95% abaixo de 0,47 s) |
| `enrich` (custo, Haiku 5.5) | US$ 1,55 | ~US$ 11,50 |

Os vetores em *int8* e as miniaturas menores cortariam o catálogo em ~40%, mas só no primeiro download (as
atualizações são deltas); foram avaliados e **não** adotados porque mudariam o formato do banco sem necessidade nesta
escala. Reavalie acima de ~50 mil famílias.

## Testes

- `dotnet test tests/GBFamilias.Core.Tests`: **162 testes**. Cobrem texto e acentos, categorias, filtros e facetas
  (inclusive `CountsAll` igual a cada faceta separada, em acervo aleatório), busca híbrida, caminhos (inclusive
  tentativas de fuga), favoritos e coleções, configurações (inclusive a de um usuário da 0.1.0), **Ed25519 (vetores da
  RFC 8032 e uma assinatura gerada pelo Rust)**, **PKCE e o login completo** (retorno pelo navegador, `state` falso,
  expiração, renovação concorrente, sessão apagada), cliente do servidor (401 e renovação, 403, 404, 429 com
  `Retry-After`, `Range`), downloads (hash, corrompido, fila de um, cancelamento, pasta de rede), cache,
  **sincronização do catálogo** (snapshot, cadeia de deltas, buraco, *rollback*, assinatura e hash adulterados,
  `min_plugin`, banco em uso intocado) e a **compatibilidade Rust ↔ C#**: um catálogo gerado de verdade pelo
  `gbfam export-catalog` e aplicado pelo plugin.
- `cargo test` em `tools/gbfam`: 12 testes do catálogo (assinatura, delta idêntico ao banco novo, cadeia, versão que só
  cresce, adulteração, caminhos perigosos).
- **Testes integrados contra o servidor**: verificações de ponta a ponta com tokens JWT reais (login, papéis, catálogo
  assinado, download com SHA-256, `Range`, limites de cota, caminhos perigosos).
- A **janela** foi exercitada fora do Revit contra o servidor real (28 verificações): primeiro uso, catálogo, download
  de uma família, marca "baixada", filtro, arquivo corrompido, atualização por delta e sessão apagada, com capturas.
- **Dentro do Revit real, nos três anos** (`./build/revit-selftest.ps1 -Year <ano>`, com o Revit fechado): o script
  instala os plugins, abre o Revit com dados isolados e um login de teste, e o plugin roda o fluxo por dentro.
  **Revit 2025, 2026 e 2027: 27/27 cada** (22/11/2026): botão Sobre único, login salvo, catálogo do instalador, busca
  de texto e por IA, **baixar uma família do servidor (SHA-256 confere), carregar e posicionar num projeto**,
  reaproveitar, sem internet (a baixada continua disponível; a nova diz "sem conexão") e sem login (pede o login).

## Publicar

```powershell
# Empacotamento completo (plugin + motor de IA + modelos + catálogo gerado com gbfam export-catalog)...
./build/package-familias.ps1 -CatalogDir <pasta-do-catalogo>
# ...ou só o catálogo, para quem já tem o plugin
./build/package-catalog.ps1 -CatalogDir <pasta-do-catalogo>
```

`gbfam.exe` e os modelos não estão no repositório: o script do plugin os procura em `tools\gbfam\` (compile com
`cargo build --release` e deixe os modelos em `tools\gbfam\.models`) e aceita `-GbfamExe` e `-Models`. Por isso o CI
só **compila** o plugin para os três anos e roda os testes do Core e do `gbfam`; o pacote é gerado na máquina de quem
publica informando `-CatalogDir` (ou a variável de ambiente `GBFAM_CATALOG_DIR`). A **chave privada de assinatura do
catálogo** fica exclusivamente fora do repositório.

`./build/package-library.ps1` continua existindo para uso interno ou ambiente local sem servidor, mas **não é publicado**.

**Instalador `.exe`:** `GBFamilias-Setup-<versão>.exe`, usando o instalador próprio `build/SetupStub.cs`. Ele confere o
SHA-256 do pacote, recusa instalar com o Revit aberto e roda o `Install.ps1`. O `.zip` com `Instalar.bat` continua
disponível; `-NoExe` gera só o `.zip`.

## Limitações conhecidas

- Os modelos de IA pesam ~1 GB (o e5 em precisão total tem 448 MB). Versões quantizadas reduziriam bastante.
- As miniaturas têm ~128 px, então a busca por imagem e as descrições têm pouco detalhe visual.
- Uma pequena fração das famílias (~3%) não tem preview embutido no `.rfa`; elas continuam pesquisáveis por nome e metadados.
- O CLIP foi treinado em inglês: consultas em português dependem sobretudo do e5 sobre a descrição gerada.
- Sem conexão com o servidor de arquivos, a busca local e as famílias já baixadas continuam funcionando, mas não é
  possível fazer login novo nem baixar famílias ainda não presentes no computador.
