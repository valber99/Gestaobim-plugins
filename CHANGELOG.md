# Changelog

Desde a **0.4.0** os plugins saem num instalador único (cinco desde a 0.5.0), com uma versão só e a tag `v<versão>`.
Antes disso, cada um tinha a sua: **GB DWG Export** com tag `dwg-v<versão>` (prefixo "DWG" abaixo), **GB Famílias**
com `familias-v<versão>` (prefixo "Famílias") e GB Material Studio com `v<versão>`.

## 0.5.5 — Revisão geral de estabilidade, UX de cadastro, múltiplos Revits e desinstalador no `.exe`

### Melhorado
- **GB Famílias (cadastro e conta na interface)**: botão **Entrar / Sair** agora visível na barra de status da biblioteca, acompanhado do botão **Criar conta** (também presente em **Configurações** e na janela de login) abrindo diretamente **[https://cadastro.gestaobim.com](https://cadastro.gestaobim.com)**; mensagem de conta pendente de aprovação atualizada com o endereço de cadastro.
- **Instalador e desinstalador**: `Install.ps1` agora copia `Desinstalar.bat` e `Uninstall.ps1` para `%APPDATA%\GestaoBIM\` (permitindo desinstalar mesmo quando instalado pelo `.exe`), aplica `Unblock-File` também nos arquivos `.addin` e remove resíduos `pending/` e `-wal`/`-shm` na desinstalação; empacotamento `.zip` migrado para `System.IO.Compression.ZipFile` (sem limite de 2 GB do PowerShell 5.1) e saída UTF-8 corrigida no instalador gráfico (`SetupStub.cs`).
- **Atualizações (`AboutHub` e `UpdateNotifier`)**: suporte à variável compartilhada `GB_NO_UPDATE_CHECK=1` para desligar a verificação nos cinco plugins, aviso correto na janela **Sobre** quando não há conexão com a internet (em vez de informar versão mais recente) e escrita atômica de `update-check.json`.
- **GB Cotas**: perfil de regras (`perfil-regras.json`) recarregado automaticamente ao clicar em **Planejar cotas** após editar via **Abrir regras…**.
- **GB Material Studio**: rotação automática de backups `.bak.*` do banco de favoritos mantendo os 5 mais recentes.
- **Documentação**: revisão completa de `README.md`, `CONTRIBUTING.md`, `docs/*` e `tools/gbfam/README.md` refletindo os 5 plugins, os painéis atuais e o fluxo de cadastro.

### Corrigido
- **GB Famílias (downloads e catálogo)**: recuperação automática de erro HTTP `416` em arquivos `.part` inválidos ou já completos, reaproveitamento de `.part` íntegro antes de baixar novamente, streaming em disco no download de snapshot do catálogo, gravação atômica de deltas e atualização de `catalog.version` no commit.
- **GB Famílias (interface e sessão)**: remoção de leitura síncrona do SQLite na UI a cada atualização da conta, retry real de sincronização do catálogo após login, abertura da pasta real para famílias reconhecidas em subpastas, atualização imediata da lista ao remover item da coleção atual, checagem estruturada `FamilyCompat.CanOpen`, proteção contra race/desconexão vazia no `LoopbackListener` e fila segura no `RevitDispatcher` quando o Revit recusa o `ExternalEvent`.
- **GBAi (`GatewayHost` e `AiClient`)**: corrigido `DeleteStaleConfigs()` que apagava o `config.yaml` de uma instância viva do Revit ao abrir uma segunda instância; payload JSON para `/v1/images/generations` sem imagens de entrada, tratamento seguro de base64 inválido/timeout e alinhamento de modelos `dall-e` e `imagen`.
- **GB Render**: corrigida pilha de elementos aninhados e colisão de chaves entre múltiplas instâncias do mesmo vínculo RVT no `SceneExtractor`, envio do MIME real em edições sucessivas, redação do token de sessão nos relatórios e decodificação WPF na thread de interface.
- **GB DWG Export**: tratamento de erro de disco/permissão ao exportar configurações (`.zip`).
- **Atualização em segundo plano**: corrigido vazamento de inscrição no evento `Idling` do Revit nos `UpdateNotifier` de Material Studio, DWG Export e Famílias quando não há versão nova.
- **Motor `gbfam` (Rust)**: corrigida detecção de marcador `FF D9` em previews JPEG, proteção contra pânico de slice em `parse_json_object` e Unicode em `sanitize`, checagem de status HTTP antes do parse JSON no `enrich` e `VACUUM INTO ?1` parametrizado.

## 0.5.4 — Novo endereço de login/cadastro (`gestaobim.com`) e melhorias de portabilidade

### Melhorado
- **GB Famílias (login e cadastro)**: autenticação atualizada para `https://auth.gestaobim.com` e cadastro de acesso à biblioteca disponível em **`https://cadastro.gestaobim.com`** (com link em destaque no início do `README.md` e em `docs/GB-FAMILIAS.md`).
- **Portabilidade e CI**: corrigido tratamento de separadores de caminho no `GBFamilias.Core` (testes 100% verdes em Windows e Linux) e generalizados caminhos padrão nos scripts de desenvolvimento e no motor `gbfam`.
- **Comunidade open-source**: adicionados `CONTRIBUTING.md`, modelos de Issues/PR e GitHub Discussions habilitado.

## 0.5.3 — Botão único do GB DWG Export e novos ícones modernos HiDPI

### Melhorado
- **GB DWG Export**: todos os comandos da faixa de opções (Exportar folhas, Model space, Layout único, Exportar vistas
  e Configurações) foram unificados em **um único botão clicável (`Exportar DWG`)** que abre diretamente a interface
  modernizada de exportação (com seleção de Folhas/Vistas, todos os modos de saída e botões *Exportar config.* /
  *Importar config.* no cabeçalho).
- **Novos ícones em todos os 5 plugins**: toda a identidade visual dos botões do Revit foi redesenhada em estilo
  moderno, consistente e representativo (`64×64` a `192 DPI`, nítido em monitores HiDPI sem cortes no Ribbon) para
  **GB Material Studio**, **GB DWG Export**, **GB Famílias**, **GB Cotas**, **GB Render** e **Sobre**.

## 0.5.2 — Nova interface, barra organizada, logo GBiM e correções no GB Render

### Melhorado
- **Barra de plugins (Ribbon)**: `Configurar IA`, `Cotar vista` e `Render com IA` agrupados no painel único **IA**;
  no **Exportar DWG**, *Exportar* e *Importar configurações* unificados no botão dropdown **Configurações**.
- **GB Famílias**: nova interface moderna com cabeçalho escuro, cards arredondados, grade responsiva (ajusta
  automaticamente o número de colunas à largura da janela), ações fixas no rodapé do painel de detalhes, filtro
  **Somente baixadas** visível e remoção do filtro redundante *Pasta de origem*.
- **GB Render, GB Cotas e Configurar IA**: interface unificada em cards com barra de progresso, pré-visualização da
  imagem renderizada/clay direto na janela do **GB Render**, carregamento automático de modelos ao abrir **Configurar
  IA** com aviso se um modelo de texto for selecionado para render, botões *Marcar/Desmarcar todas* no **GB Cotas** e
  cancelamento seguro ao fechar qualquer janela.
- **Janela Sobre (`AboutHub`)**: aviso de atualização e botão **Baixar Gestão BIM** exibidos uma única vez para o
  instalador único.
- **Instalador (`GestaoBIM-Setup-0.5.2.exe`)**: novo ícone multi-resolução e logo **GBiM** no cabeçalho da janela.

### Corrigido
- **GB Render**: corrigido erro `400` de `aspect_ratio` mapeando a proporção da vista 3D para o valor suportado mais
  próximo na API Gemini, com fallback automático sem `imageConfig` para modelos que não habilitam `aspect_ratio`, e
  seleção automática de modelo de imagem caso um modelo de texto esteja salvo.

## 0.5.1 — Instalador gráfico, busca IA em RAM e correções de estabilidade

### Novo
- **Instalador gráfico (`GestaoBIM-Setup-0.5.1.exe`)**: interface nativa do Windows (sem abrir janela preta de CMD),
  com seleção das versões do Revit (2025, 2026 e 2027), barra de progresso real, painel de detalhes expansível e
  suporte completo a `/quiet`.

### Melhorado
- **GB Famílias (`gbfam.exe`)**: os vetores das 41.762 famílias agora são carregados uma única vez em memória RAM
  (`VectorIndex` contíguo), eliminando leituras repetidas do SQLite e tornando a busca semântica quase instantânea;
  banco aberto em modo somente leitura (`READ_ONLY`) e ordenação segura com `total_cmp`.
- **GB Famílias**: buscas sem filtros laterais não serializam mais os 41.762 IDs em JSON; abertura da biblioteca não
  trava mais a UI com varredura síncrona de pasta de downloads.

### Corrigido
- **GB Famílias**: corrigido erro *"The command to place an instance of this family is disabled"* e o travamento do
  Revit ao inserir famílias MEP/perfis/anotações (captura de `Autodesk.Revit.Exceptions.InvalidOperationException` e
  aviso WPF vinculado à janela do plugin).
- **GB Famílias**: corrigido lock em `LibraryDb.Dispose()`, crash ao fechar a janela durante download/inserção,
  botão "Aplicar agora" de atualização de catálogo e streaming atômico em disco no `CatalogSync`.
- **GB IA / Cotas / Render**: escape de `\r`/`\n` no `config.yaml`, proteção contra perda de `keys.dat` em
  `IOException` transitório, timeout de 2 s no `Mutex` ao fechar o Revit, validação de `ProcessName` em `KillPid`,
  tratamento de `NewDimension` nulo e eixos invertidos no `GB Cotas`, e `OnLinkBegin` seguro no `GB Render`.
- **Instalador (`Install.ps1` / `SetupStub.cs`)**: suporte a múltiplas versões do Revit selecionadas, cópia atômica
  do catálogo, caminhos absolutos do Windows e limpeza garantida de arquivos temporários em `finally`.

## 0.5.0 — GB Cotas e GB Render (IA) no instalador único

### Novo
- **GB Cotas** (painel Cotas): cota a planta aberta com **cotas nativas do Revit**. Pega faces
  de paredes, aberturas e eixos e monta as cadeias interna, intermediária e externa de cada lado. Afasta as linhas
  para que não se sobreponham entre si, às paredes, tags e cotas existentes. Funciona **só por regras, sem IA**. Com IA,
  o modelo sugere quais cadeias criar a partir do pedido ("cote as fachadas e os banheiros por dentro") e o plugin
  confere cada referência antes de cotar. O que não passa é recusado com o motivo.
  - Antes de gravar, mostra a lista de cadeias para marcar e desmarcar. Grava numa transação só (um Ctrl+Z desfaz
    tudo), relê cada cota e desfaz a cadeia se o Revit medir algo diferente do plano. Salva um `autodim_audit.json`.
  - Perfil de regras editável (afastamentos em mm de papel, lados, faixa da fachada) em `%APPDATA%\GestaoBIM\Cotas`.
  - Plantas giradas: as cotas seguem a direção dominante das paredes.
  - Cortes e elevações ainda não são cotados: a vista é recusada com o motivo.
- **GB Render** (painel Render): render da vista 3D aberta com um modelo de imagem. O plugin rasteriza a geometria e
  gera quatro camadas alinhadas pixel a pixel: clay, cores por elemento, profundidade e fundo. Junta a elas um
  manifesto com a categoria, a família, o material e a caixa na tela de cada objeto. **Edição pontual**: marque objetos
  e peça a troca só deles. O plugin confere se a imagem mudou fora da máscara.
  Perspectivas precisam estar com **Recortar vista** ligado (sem recorte, o Revit usa um enquadramento que a API não
  informa).
- **Configurar IA** (painel IA): conecta uma **chave de API** (Gemini, Claude ou OpenAI, guardada com DPAPI) ou uma
  **assinatura** (Claude Code, ChatGPT Codex, Antigravity), escolhe o modelo de cada tarefa e testa a conexão. Entrar
  com assinatura mostra antes o aviso sobre os termos de uso do provedor.
- Gateway local: o **CLIProxyAPI 8.1.1** original (MIT), conferido por SHA-256. Escuta só em `127.0.0.1`, usa uma senha
  nova por sessão e um limite de 64 MB por pedido. O painel de gerenciamento fica desligado e a pasta de contas só pode
  ser lida pelo usuário. Ele fecha junto com o Revit.

### Alterado
- **GB Famílias**: catálogo atualizado para a versão **4** com **41.762 famílias únicas** (40.328 com preview embutido),
  100% enriquecidas com descrição, categoria e tags por IA e indexadas para busca semântica por texto e imagem.
- O instalador único leva os **cinco** plugins, com uma versão só (0.5.0), e um `THIRD-PARTY.txt` com as licenças de
  terceiros.
- O desinstalador remove também o gateway de IA. As contas conectadas ficam.

## 0.4.1 — GB DWG Export: folhas completas no DWG mesclado

### Corrigido
- GB DWG Export: a folha mesclada saía **sem carimbo, tabelas, textos e legendas**. O layout só enquadrava o model
  space, e tudo o que o Revit põe no espaço de papel se perdia. Agora cada layout é **o espaço de papel da folha**,
  copiado do DWG que o Revit exporta, com as viewports apontando para as vistas no model space.
- GB DWG Export: com uma configuração de exportação do projeto que grava as vistas como xref (`MergedViews` desligado),
  as vistas sumiam do desenho mesclado. Ao juntar, as vistas agora são sempre mescladas na folha.
- GB DWG Export: o papel do layout vem do **carimbo** da folha. O contorno da folha, usado antes, inclui as vistas
  deixadas fora do carimbo e dava um papel enorme (2824 x 1544 mm numa folha A2).
- GB DWG Export: as imagens que o Revit grava ao lado do DWG (logos, renders) são copiadas para a pasta do desenho
  mesclado.
- GB DWG Export: uma mesclagem com muitas folhas é dividida em várias execuções do motor, para não passar do limite
  da linha de comando do Windows.

### Alterado
- GB DWG Export: o **model space** junta todas as vistas, de todas as folhas, lado a lado (plantas, cortes, fachadas).
- GB DWG Export: o **layout único** põe as folhas inteiras lado a lado numa só página.
- GB DWG Export: **Exportar vistas** junta as vistas marcadas num só DWG, lado a lado no model space (o padrão), ou
  grava um arquivo por vista.
- GB DWG Export: os layouts Layout1 e Layout2 vazios não aparecem mais no desenho mesclado.

## 0.4.0 — instalador único Gestão BIM

A partir desta versão os três plugins saem juntos, com **uma versão só** e a tag `v<versão>`.

### Alterado
- **Instalador único** `GestaoBIM-Setup-0.4.0.exe`: os três plugins, o catálogo de famílias (banco com previews e
  dados de busca) e a busca por IA (`gbfam.exe` e modelos). Só os `.rfa` das famílias ficam de fora (vêm do servidor).
  Não há mais instaladores separados por plugin.
- GB Famílias, **Configurações** com só três coisas: a **pasta das famílias**, o **espaço usado** (**Sem limite**, o
  novo padrão, ou **Limitar a N GB**) e a **conta**. Saíram da tela o modo "Pasta local (avançado)", o caminho do
  `gbfam.exe` e a chave da busca por IA (a busca por IA está sempre disponível; a janela da biblioteca tem a chave dela).
- GB Famílias, **pasta onde você já tem famílias**: o plugin reconhece as que já estão lá (pelo caminho da biblioteca
  ou, se estiverem organizadas do seu jeito, pelo nome do arquivo e o tamanho), confere o SHA-256 ao inserir e não
  baixa de novo.
- GB Famílias: o **modo local saiu**. Quem usava a 0.1 com a biblioteca numa pasta passa ao servidor sozinho, com a
  pasta da biblioteca como pasta das famílias (tudo o que já tinha continua disponível).
- O aviso de versão nova dos três plugins usa a release única (`v*`).

### Corrigido
- GB Famílias: **Configurações → Entrar** dava erro ("Não é possível definir a propriedade Owner para uma Window que não
  tenha sido exibida antes"). O login abre por cima da própria janela de Configurações.
- GB Famílias: com limite de espaço, a limpeza podia apagar famílias que o usuário já tinha na pasta. Agora só apaga
  as que o próprio plugin baixou.

### Testes
- 168 testes xUnit do GB Famílias (eram 163), 74 do DWG Export, 51 do Material Studio, 12 em Rust.
- Teste dentro dos Revit 2025, 2026 e 2027 instalado pelo `.exe` único, com as verificações novas: login com a janela
  de andamento, família que o usuário já tinha em outra subpasta (usada sem login e sem baixar) e a limpeza do cache.

## DWG 0.1.1 e Material Studio 0.3.2

### Alterado
- **Um só botão "Sobre"** para todos os plugins Gestão BIM, no painel **Geral** (antes cada plugin tinha o seu). A janela
  mostra a versão, o aviso de versão nova e o diagnóstico de cada plugin instalado; a bolinha vermelha acende se qualquer
  um tiver versão nova. Testado nos Revit 2025, 2026 e 2027.
- **Instalador `.exe` novo** (o mesmo dos três plugins): confere o SHA-256 do pacote antes de instalar, não pede
  administrador e aceita `/quiet`. Substitui o do IExpress.

## Famílias 0.2.0

### Novo
- **O catálogo vai dentro do instalador** (previews, descrições e dados da busca por IA): a busca funciona sem internet
  já na primeira abertura. O **servidor só entrega os `.rfa`**: cada um é baixado quando você insere a família, um de
  cada vez, com barra de andamento, **Cancelar**, retomada (`Range`) e conferência de integridade (SHA-256 do
  catálogo). O que já foi baixado funciona sem internet.
- **Instalador `.exe`** (`GBFamilias-Setup-<versão>.exe`): confere o pacote (SHA-256), recusa instalar com o Revit
  aberto e instala em todos os Revit 2025, 2026 e 2027 encontrados. O `.zip` com `Instalar.bat` continua disponível.
- Pacote pequeno **`GBFamilias-Catalogo`** para atualizar só o catálogo (o instalador só troca se a versão for mais nova).
- **Pasta das famílias baixadas obrigatória**, de livre escolha (local ou de rede compartilhada: dois computadores
  baixando o mesmo arquivo não se atrapalham). Limite do cache em GB (nunca limpa pasta de rede).
- **Login** pelo navegador (OAuth 2.0 + PKCE, sem segredo no plugin); a sessão fica protegida pela DPAPI do Windows.
- **Atualização do catálogo pelo servidor (opcional, desligada: `catalog_sync`)**: assinada (Ed25519) e por delta; recusa
  assinatura que não confere, arquivo adulterado e versão mais antiga; nunca troca o banco em uso.
- Marca **✓ baixada** nos cartões, filtro **Somente baixadas**, botão **Baixar e inserir**, conta e versão do
  catálogo na barra de status.
- Contagem das facetas numa passada só: 5 vezes mais rápida (18 ms com 25 mil famílias).
- `gbfam`: `gen-key`, `export-catalog`, `verify-catalog`, `check-store`; `enrich` agora mostra tokens e custo.

### Alterado
- **Um só botão "Sobre"** para os três plugins Gestão BIM (painel **Geral**), em vez de um por plugin: a janela mostra a versão,
  o aviso de atualização e o diagnóstico de cada plugin instalado. Vale para o Material Studio e o DWG Export a
  partir da próxima versão de cada um (o código compartilhado está em `src/GBShared`).
- O pacote completo da biblioteca (1,4 GB) **deixou de ser publicado**: o acesso passa pelo servidor, com login e limites.
  Quem já usa a 0.1.0 continua no modo local, com as configurações que tinha.
- O código do motor `gbfam` passou a ficar no repositório (`tools/gbfam`); banco, modelos e segredos continuam de fora.
- O backend de arquivos é mantido separadamente do repositório público dos plugins.
- Revit 2025, 2026 e 2027 (sem mudança).

### Corrigido
- O catálogo levava os caminhos absolutos do computador do administrador; agora só caminhos relativos.
- O servidor entregava qualquer arquivo da pasta do catálogo a quem tinha login (inclusive o mapa interno); agora só o que o plugin usa.
- A biblioteca não abria sem a pasta de downloads; agora a pasta é pedida só ao baixar.
- O *pool* de conexões do SQLite mantinha o banco aberto depois de fechar e impedia a troca do catálogo no Windows.
- Cancelar um download enquanto ele esperava na fila deixava o contador de espera errado.

### Testes
- 163 testes xUnit (eram 70), 12 do catálogo em Rust e, no servidor, 42 em Go com `-race`; teste de ponta a ponta
  contra o servidor real (22 verificações) e da janela contra ele (28), com tokens JWT reais.

### Limitações
- Testado por dentro dos Revit 2025, 2026 e 2027 reais (27/27 em cada: baixar, carregar e posicionar, sem internet, sem
  login, Sobre único); a janela em si foi exercitada fora do Revit, então vale um uso manual curto antes de distribuir.
- Os modelos de IA pesam ~1 GB. Confira os direitos de redistribuição das famílias antes de compartilhar acesso.

## Famílias 0.1.0

### Novo
- **Plugin GB Famílias**: biblioteca de famílias do Revit na aba **Gestão BIM**. Busca por texto (sem acento, com
  pontuação por onde a palavra casou) e **busca por IA** que entende o sentido e aceita **imagens**; filtros por
  categoria (árvore com subcategorias), pasta de origem, ambiente, material, cor, estilo, versão e categoria do Revit,
  com contagens que acompanham os outros filtros; **favoritos, coleções e recentes**; detalhes com descrição,
  etiquetas e aviso de compatibilidade de versão; **inserir no projeto** com posicionamento.
- A IA roda no computador do usuário (CPU), em um processo filho (`gbfam serve`), sem enviar nada para fora. Se ela
  não estiver disponível, a busca por texto e os filtros continuam funcionando.
- Dois pacotes `.zip` com `Instalar.bat`: o plugin (com o motor de IA e os modelos, offline) e a biblioteca
  (banco + 3.368 famílias únicas).
- Mesma infraestrutura dos outros plugins: aba compartilhada, `Install.ps1` genérico, botão **Sobre** com aviso de
  versão nova, 70 testes xUnit.

### Limitações
- A camada do Revit (carregar e posicionar a família) não foi executada em um Revit real; a janela foi exercitada fora
  do Revit contra a biblioteca real.
- Os modelos de IA pesam ~1 GB.
- Sem instalador `.exe`: o IExpress travou com um payload desse tamanho.

## DWG 0.1.0

### Novo
- **Plugin GB DWG Export**: exporta folhas do Revit para **um só DWG, com um layout por folha no mesmo papel que a
  folha usa no Revit** (também model space e layout único, ou um arquivo por folha), e vistas para DWG/DXF, sem
  AutoCAD. Usa a configuração de exportação do projeto.
- A mesclagem usa o `cadcraft-cli` 0.20.1 original, embutido no instalador e conferido por SHA-256.
- Lista de folhas em ordem natural com busca, nomes de arquivo seguros, verificação do limite de 259 caracteres do
  caminho (com a Área de Trabalho como alternativa), confirmação antes de sobrescrever, relatório final com falhas e
  perdas avisadas pelo motor.
- Exportar e importar configurações (`.zip` com manifesto), botão **Sobre** com aviso de versão nova.
- O instalador (`Install.ps1`) agora instala qualquer plugin cujo `.addin` esteja no pacote e copia ferramentas
  compartilhadas para `%APPDATA%\GestaoBIM\Tools`.

## 0.3.1

### Novo
- **Instalador `.exe`** (`GBMaterialStudio-Setup-<versão>.exe`): dois cliques instalam em todos os Revit 2025, 2026
  e 2027 encontrados; recusa instalar com o Revit aberto. O `.zip` com `Instalar.bat` continua disponível.
- Documentação: guia do usuário, arquitetura do add-in nativo e processo de release em `docs/`.
- **Botão "Sobre" com aviso de atualização.** Mostra a versão instalada e consulta a última release no GitHub
  na hora; se houver versão nova, oferece "Baixar a nova versão" (abre a página da release).
- Ao abrir o Revit, o add-in faz essa consulta em segundo plano (no máximo uma vez por dia) e, se houver versão
  nova, mostra uma **bolinha vermelha** no botão. Sem internet, nada acontece.
  Para desligar a consulta automática: variável de ambiente `GB_MATERIAL_STUDIO_NO_UPDATE_CHECK=1`.

## 0.3.0

### Novo
- **Add-in nativo (C#/.NET).** O GB Material Studio agora é um add-in do Revit que não depende de pyRevit
  nem de Python. Um código-fonte, três compilações: Revit 2025, 2026 e 2027.
- Instalador `Instalar.bat`/`Desinstalar.bat` (sem Python) e pacote `.zip` com checksum SHA-256.
- Favoritos e biblioteca da edição pyRevit são copiados para `%APPDATA%\GestaoBIM\MaterialStudio` na primeira abertura.
- 41 testes xUnit para as regras puras e o banco de favoritos; CI compila as três versões.

### Observações
- O add-in não é assinado: o Revit pergunta na primeira vez, escolha "Sempre carregar".
- A edição pyRevit continua no repositório como legado.

## 0.2.0

### Alterado
- **Python 3.** A extensão migrou do IronPython 2.7 (fim de vida) para o motor CPython 3.12 do pyRevit.
  Todo botão começa com `#! python3`. Exige pyRevit 5 ou mais novo.
- Como o `pyrevit.forms` não existe no CPython, as janelas e diálogos agora usam `mt_wpf` e `mt_dialogs`
  (alerta, texto, arquivo, cor e lista com filtro).
- A DLL do cursor virou `GBCursorHook.dll` (`GestaoBim.Native.CursorHook`).
- `imp` foi trocado por `importlib` no carregador; o código de compatibilidade com Python 2 foi removido.

### Testes
- O guarda de sintaxe do IronPython 2.7 foi substituído por guardas do CPython: sem `pyrevit.forms`, sem
  `imp`, todo botão com `#! python3`, XAML bem formado e todos os módulos listados no carregador.

## 0.1.0

Primeira versão do repositório. A extensão passa a se chamar `GBMaterialStudio.extension` (antes
`ValberMaterialStudio.extension`).

### Novo
- **Biblioteca portátil:** favoritos são copiados para uma biblioteca (`.rvt` por versão do Revit) e
  transferem para qualquer projeto sem abrir o de origem. Pasta configurável em `gb-settings.json`.
- **Organizar materiais:** uso real por material, duplicados, exclusão de não usados e renomeação em lote.
- **Aplicar em tipo:** troca de material em camadas de tipos de parede, piso, cobertura e forro.
- **Editor fixo:** o editor numa janela que não bloqueia o Revit.
- Cursor do conta-gotas novo (32 bits, 3 tamanhos, contorno) e sem piscar.

### Corrigido
- Escolher um padrão de modelo no corte derrubava toda a etapa de gráficos; o corte agora só lista
  padrões de desenho.
- Aplicar o editor é uma única entrada de desfazer e é tudo ou nada, com o motivo da falha.
- Renomear com nome repetido ou vazio era ignorado sem aviso.
- Duplicar material nunca copiava a aparência.
- Escopos de edição de aparência ficavam abertos quando havia erro.
- Pintar, novo material, abrir favorito e editor de textura abriam janelas dentro do evento de outra.
- O botão Editar material e a pintura engoliam qualquer erro como se fosse Esc.
- Favoritos deixavam de ser reconhecidos ao mover ou renomear o projeto.
- O cache de pré-visualização de padrões nunca funcionava no Revit 2027 (`IntegerValue` removido).

### Testes
- Suíte pytest para as regras puras, as configurações e o banco de favoritos, mais um guarda de
  sintaxe do IronPython 2.7, executados no GitHub Actions.
