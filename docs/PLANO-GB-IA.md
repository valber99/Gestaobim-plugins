# Plano: dois plugins de IA para o Revit (Render semântico e Cotagem automática)

Plano para trazer ao Gestão BIM a proposta do documento *Arquitetura de Dois Plugins de IA para Autodesk Revit*
(HTML recebido em 22/11/2026). Ele confronta a proposta com as decisões que o repositório já tomou, fixa a
arquitetura, as fases e os critérios de aceite. **Executado na 0.5.0**: o que foi feito, o que mudou em relação ao plano
e o que foi medido está no [item 9](#9-execução-050).

- Plugins existentes que servem de molde: [`GB-FAMILIAS.md`](GB-FAMILIAS.md), [`GB-DWG-EXPORT.md`](GB-DWG-EXPORT.md),
  [`ARQUITETURA.md`](ARQUITETURA.md).
- Nomes provisórios: **GB Render** (plugin 1) e **GB Cotas** (plugin 2). Código compartilhado: `src/GBAi.Shared`.

## 1. O que a proposta pede

1. **Gateway de IA local.** Um processo à parte (o documento sugere o `CLIProxyAPI`, em Go) em `127.0.0.1:8317`. Ele
   cuida do login OAuth nas contas Antigravity, ChatGPT Codex e Claude Code, faz rodízio entre contas, traduz entre as
   APIs OpenAI, Anthropic e Gemini e mostra as cotas. O Revit fala só com ele, e um painel WebView2 o configura.
2. **Plugin 1, Render semântico.** Exporta da vista 3D quatro camadas alinhadas: *Clay* (base sombreada), *Color-ID*
   (uma cor por elemento), profundidade e máscara de fundo. Junta um manifesto JSON com família, tipo, fabricante,
   material e a caixa de cada objeto na imagem, mais as miniaturas do catálogo do GB Famílias. Envia tudo a um
   modelo de imagem (Gemini "Nano Banana"), direto ou orquestrado por um ComfyUI sem interface.
3. **Plugin 2, Cotagem automática (neuro-simbólica).** O código C# extrai as `Reference` válidas da vista e dá um ID a
   cada uma. O LLM devolve **apenas um plano JSON** (quais cadeias, em que ordem, com que convenção de face). Um solver
   C# calcula os afastamentos, evita colisões (AABB), mostra um preview e grava cotas nativas `Dimension`.
4. **Entrega em quatro fases** com critério de pronto verificável por máquina.

A ideia central dos dois plugins está certa e é o que este plano mantém: **a IA nunca inventa geometria**. Ela
recebe dados extraídos do modelo e devolve intenção (prompt, plano). O que vai para o desenho é calculado por código
determinístico.

## 2. Divergências com o repositório e a posição deste plano

| Tema | Proposta | Repositório hoje | Posição deste plano |
|---|---|---|---|
| Versões do Revit | 2022–2027 (`net48` + `net8`) | 2025–2027 (`net8.0-windows` e `net10.0-windows`) | **2025, 2026 e 2027.** Sem `net48`. |
| DLL compartilhada | `RevitAI.Core.dll` carregada pelos dois add-ins | Cada add-in é **uma DLL só**; o código comum (`src/GBShared`) é compilado dentro de cada uma | **Compilar dentro**, como o `GBShared`. Duas cópias de uma mesma DLL em pastas diferentes, carregadas no mesmo Revit, entram em conflito de versão. |
| Processo de IA | Sidecar Go de terceiros | O GB Famílias já sobe `gbfam serve` (Rust): HTTP só em `127.0.0.1`, porta livre, token aleatório por sessão, encerra quando o `stdin` fecha | **Reusar esse padrão** para o gateway (ver 3.2). |
| Login nas IAs | OAuth de assinaturas pessoais (Claude Code, Codex, Antigravity) por proxy | Nada | **OAuth de assinatura e chave de API, pelo `CLIProxyAPI`** (decisão do usuário em 22/11/2026, com o risco de 7.1 aceito). |
| Painel de configuração | WebView2 | Janelas WPF/XAML em todos os plugins | **WPF**, como o resto. O WebView2 traria outro runtime e outro estilo visual sem ganho real. |
| Catálogo | `catalog.db` offline com previews | Já existe no GB Famílias (SQLite, previews PNG, hash de conteúdo) | Ler o catálogo do GB Famílias **só para leitura**, se estiver instalado. |
| Normas de cotagem | ABNT NBR 6492 | Nada | Mantido, como **perfil de regras** em arquivo (ver 4.3). |

## 3. Arquitetura alvo

```
Revit (2025–2027)
 ├─ GBRender.dll   (plugin 1)  ┐  cada um compila dentro de si
 ├─ GBCotas.dll    (plugin 2)  ┘  src/GBAi.Shared + src/GBShared
 │        │  HTTP 127.0.0.1, porta livre, token por sessão
 ▼        ▼
CLIProxyAPI.exe  (processo filho; um por sessão do Windows; versão travada)
 ├─ provedores por OAuth de assinatura: Claude Code, ChatGPT Codex, Antigravity
 ├─ provedores por chave de API: Anthropic, OpenAI, Google (Gemini / Vertex), OpenRouter
 ├─ roteamento por tarefa (render, planejar cotas, traduzir prompt) e ordem de reserva
 ├─ registro de uso e custo, sem guardar chave nem imagem no log
 └─ (opcional) ComfyUI local em 127.0.0.1:8188, modo orquestrado do render
```

### 3.1 Código

```
src/GBAi.Shared           sem Revit: cliente do gateway, contratos JSON, regras puras, testado no CI
src/GBRender              add-in do plugin 1 (Revit/, UI/, Addin/) + Core próprio quando crescer
src/GBCotas               add-in do plugin 2
src/GBCotas.Core          solver de cotas (planejamento → posições, colisão AABB): puro, sem Revit
tests/GBAi.Shared.Tests, tests/GBCotas.Core.Tests
tools/gbai-gateway        config.yaml endurecido, SHA-256 do binário travado, script que baixa e confere a versão
```

O padrão é o mesmo do GB DWG Export: o que não precisa do Revit (validação do plano JSON, solver, projeção 3D→2D,
montagem do manifesto) fica em Core puro, com testes xUnit. A camada do Revit fica fina.

### 3.2 Gateway

- **Ciclo de vida:** igual ao `gbfam serve`. O primeiro plugin que precisar sobe o processo (mutex nomeado
  `Local\GestaoBIM.GbAiGateway`), grava porta e token num arquivo em `%LOCALAPPDATA%\GestaoBIM\gbai\` com ACL só do
  usuário, e o processo encerra quando o último Revit que o usou fechar.
- **Rede:** escuta **somente em `127.0.0.1`**, recusa qualquer pedido sem `Authorization: Bearer <token>` (401), e não
  tem CORS. Isso cobre o tipo de falha citado na proposta (GHSA-8gmq-j984-vp4r, desvio de autenticação num proxy
  local).
- **Chaves de API:** o plugin as guarda com **DPAPI** (escopo do usuário) e só as escreve no `config.yaml` da sessão,
  que fica na pasta protegida de 3.2.1 e é apagado quando o processo encerra. A janela mostra só "configurada / não
  configurada".
- **Binário adotado: `router-for-me/CLIProxyAPI`** (Go, MIT, compatível com a AGPL-3.0 dos plugins). Conferido em
  22/11/2026: versão `v8.1.1`, `CLIProxyAPI_8.1.1_windows_amd64.zip` e `checksums.txt` nos *releases*. O binário
  entra no instalador com **SHA-256 fixo**, nunca é baixado nem atualizado em tempo de execução, e cada troca de versão
  passa por revisão (o projeto lança versão quase todo dia).
- **O que o plugin fala com ele:** as APIs compatíveis com OpenAI, Anthropic e Gemini que ele já expõe, mais a
  Management API (`/v8/management`) para a janela "Configurar IA" disparar o login OAuth e ler cotas. A interface
  `IAiClient` em `GBAi.Shared` esconde isso: os plugins pedem "planejar" ou "gerar imagem" e não sabem qual provedor
  respondeu.

#### 3.2.1 `config.yaml` endurecido (os padrões do CLIProxyAPI não são seguros)

O `config.example.yaml` da v8 vem com padrões pensados para servidor, não para o PC do arquiteto. O plugin gera o
`config.yaml` a cada início e **recusa subir** se algum destes valores divergir:

| Chave | Padrão do projeto | Valor do Gestão BIM | Por quê |
|---|---|---|---|
| `server.host` | `""` (todas as interfaces) | `"127.0.0.1"` | Sem isso, qualquer máquina da rede usa as contas do arquiteto |
| `server.port` | `8317` | porta livre escolhida no início | Dois Revits e outros programas não colidem |
| `server.max-request-body-bytes` | `0` (sem limite) | `67108864` (64 MB, cabe o render 4K) | Alerta GHSA-4w5v-m7vx-jpmh (memória esgotada sem autenticação) |
| `server.discovery.enabled` | `false` | `false` | Não anunciar o serviço na rede por mDNS |
| `access` → `api-keys` | exemplos | **um token aleatório por sessão** | O 401 sem token da fase 1 |
| `management.allow-remote` | `false` | `false` | |
| `management.secret-key` | `""` (API desligada) | aleatório por sessão | A janela de configuração precisa dela |
| `management.disable-control-panel` | `false` | `true` | Senão ele baixa o painel web do GitHub e o serve localmente |
| atualização automática do painel | ligada | desligada | Nada é baixado em tempo de execução |
| `oauth.auth-dir` | `~/.cli-proxy-api` | `%LOCALAPPDATA%\GestaoBIM\gbai\auth`, ACL só do usuário | Os tokens OAuth ficam em arquivo; ver abaixo |
| `observability` (`request-log`, `logging-to-file`) | `false` | `false` | O log de requisição gravaria prompts e imagens do projeto |
| `plugins` | — | nenhum | Menos superfície |

**Tokens OAuth em disco.** O CLIProxyAPI guarda os tokens como arquivos no `auth-dir`, sem DPAPI. A proteção é a ACL
da pasta (só o usuário e o SYSTEM) e a pasta fora de qualquer sincronização (OneDrive). Cifrar com DPAPI exigiria
alterar o código dele; fica registrado como risco aceito em 8.

**Alertas conhecidos.** Os dois alertas de alta gravidade de 27/10/2026 (GHSA-4w5v-m7vx-jpmh e GHSA-j4xx-x47j-7h54)
são de negação de serviço sem autenticação. Com `127.0.0.1` só um programa local consegue explorá-los, mas a versão
travada tem que ser posterior à correção, e a fase 1 confere isso. Os alertas do projeto entram na revisão de cada
troca de versão.

## 4. Plugin 2: GB Cotas (primeiro a ser feito)

Vem primeiro porque **é quase todo determinístico**: só uma etapa usa IA, o resultado é verificável no próprio Revit
(cota nativa ou não, colisão ou não) e não depende da qualidade de um modelo de imagem.

### 4.1 Extrator (Revit)

- Vistas aceitas: `ViewPlan`, `ViewSection` e elevações. As vistas 3D ficam fora.
- Paredes: faces externas e internas, núcleo e eixo, via `HostObjectUtils.GetSideFaces` e referências de
  `Wall.Location`. Portas e janelas: `FamilyInstance.GetReferences(FamilyInstanceReferenceType.CenterLeftRight /
  Left / Right)`. Eixos (`Grid`) e pilares.
- Vínculos (`RevitLinkInstance`): `Reference.CreateLinkReference`. Fica na fase 4b, porque cota em vínculo tem
  limitações próprias.
- Cada referência recebe um ID curto (`r01`, `r02`…) e entra num **resumo da vista**: escala, orientação, paredes
  agrupadas por direção, aberturas, e as caixas ocupadas por tags e cotas que já existem. A IA vê os IDs, nunca
  coordenadas que ela possa alterar.

### 4.2 Planejador (IA)

- Entrada: o resumo da vista, o perfil de regras (4.3) e o pedido do usuário (ex.: "cote as fachadas em três linhas e
  os banheiros por dentro, executivo 1:50").
- Saída: **um JSON validado por esquema**. Uma lista de cadeias, cada uma com os IDs de referência em ordem, a
  direção, o nível (interna, intermediária ou externa) e o lado da vista. Um ID que não existe, uma cadeia com
  referências não paralelas ou um JSON fora do esquema **são recusados** e reenviados uma vez com o erro. Na segunda
  falha, a cadeia some do preview com o motivo.
- **Sem IA também funciona:** um planejador por regras gera as três cadeias clássicas da fachada. Ele serve de modo
  offline e de referência para medir se a IA acrescenta valor.

### 4.3 Perfil de regras

Um arquivo JSON versionado por escritório: afastamentos em milímetros de papel, distância entre linhas de cota, quais
faces cotar (acabamento ou núcleo), arredondamento e o que fazer com vãos curtos. Vem com um perfil padrão baseado na
NBR 6492. Assim "padrão do escritório" vira dado, não prompt.

### 4.4 Solver e gravação

- Converte afastamento em papel × escala da vista. Posiciona cada linha e testa colisão 2D (AABB) contra paredes,
  tags, textos e cotas existentes; se colidir, empurra a linha para fora e tenta de novo, com limite de tentativas.
- Preview na vista com `DirectContext3D` ou elementos temporários, numa janela que não bloqueia o Revit (o
  `RevitDispatcher` com `ExternalEvent` que já existe). O usuário marca e desmarca cadeias.
- Grava tudo numa `TransactionGroup` com `IFailuresPreprocessor`. Avisos conhecidos são tratados, e os outros
  **cancelam a cadeia**, não a operação toda. Depois de gravar, cada cota é relida: se `Dimension.NumberOfSegments`
  não bate com o plano, a cadeia é desfeita e relatada.

### 4.5 Critério de pronto (fase 4)

Numa planta de teste versionada (`tests/fixtures/cotas-planta-01.rvt`), rodando pelo autoteste no Revit (o mesmo
esquema do `revit-selftest-dwg.ps1`): **0 exceções da API**, **0 colisões AABB** entre linhas de cota, **100% das cotas
como `Dimension` nativas** e associativas, e um `autodim_audit.json` com contagem por cadeia. O teste roda com o
planejador por regras (não depende de rede) e, à parte, com a IA.

## 5. Plugin 1: GB Render

### 5.1 Passes

| Passe | Como | Cuidado |
|---|---|---|
| 1 · Clay | `View3D` temporária, estilo sombreado, sem anotações; `Document.ExportImage` em 1K/2K | Câmera, FOV e proporção copiados da vista do usuário |
| 2 · Color-ID | A mesma vista com `DisplayStyle.FlatColors` e `OverrideGraphicSettings` sólido, uma cor por elemento | **O `ExportImage` suaviza bordas.** A máscara terá cores misturadas na borda. É preciso quantizar cada pixel para a cor de ID mais próxima ou gerar a máscara no rasterizador do passe 3. |
| 3 · Profundidade | `CustomExporter` + `IExportContext` coleta as malhas; um **rasterizador próprio em C#** (z-buffer) desenha profundidade e, de quebra, uma Color-ID sem suavização | O `CustomExporter` dá triângulos, não imagem: a rasterização é nossa. É o maior trabalho do plugin. |
| 4 · Fundo | Pixels sem geometria = céu; categoria `OST_Topography`/`OST_Site`/`OST_Planting` = entorno | Vidros: decidir se contam como "abertura para o fundo" |

O manifesto JSON segue o exemplo da proposta: câmera, camada de fundo e, por objeto, o ID, a cor na máscara, a
categoria, família e tipo, fabricante, material e `screen_bbox_uv` (caixa projetada). A projeção 3D→2D e a montagem
ficam no Core, com testes.

### 5.2 Modos de geração

- **A · Direto pelo gateway:** passes + manifesto + até 3 miniaturas do catálogo → modelo de imagem. Edição pontual
  depois (trocar só a poltrona, só o fundo) reenviando o render e a máscara daquele objeto.
- **B · ComfyUI local (opcional):** compõe máscaras e recorta o fundo de forma determinística, e chama o modelo
  fechado por um nó. Só entra se o modo A mostrar deslocamento de geometria que incomode na prática.
- **C · ControlNet local (Flux/SDXL):** fora deste plano. Exige GPU de 16–24 GB.

Os nomes e limites de modelo da proposta (Nano Banana Pro / 2.1, até 14 referências, saída 4K) **precisam ser
conferidos na documentação oficial do Google na fase 3**. O plugin não depende deles: o modelo de render é uma
configuração do gateway.

### 5.3 Critério de pronto

- Fase 2: os quatro PNG com a mesma resolução e **100% das cores da máscara presentes no manifesto** (e vice-versa,
  descontando objetos ocultos). Tudo verificado por script num `render_bundle.zip`.
- Fase 3: render 2K gerado com `render_report.json` (hashes de entrada e saída, modelo, custo) **sem chave nem token no
  log**. Mais um teste de edição isolada: só a região da máscara escolhida muda acima de um limiar de diferença.

## 6. Fases

| Fase | Escopo | Pronto quando | Estimativa |
|---|---|---|---|
| 0 | Decisões do item 7; nomes, ícones e `AddInId`; perfil de regras padrão | Item 7 respondido | 0,5 dia |
| 1 | CLIProxyAPI travado + `config.yaml` endurecido (3.2.1) + `src/GBAi.Shared` + janela WPF "Configurar IA" (login OAuth, chaves, modelo por tarefa, cotas, teste de conexão) | Teste automatizado: escuta só em `127.0.0.1` (porta recusada no IP da rede); 401 sem token; 413 acima do limite; 200 em `/v1/models` com um provedor por OAuth; ACL do `auth-dir`; SHA-256 do binário no `gateway-health.json` | 4 dias |
| 4a | GB Cotas: extrator, planejador por regras, solver, preview, gravação | Critério 4.5 com o planejador por regras | 5 dias |
| 4b | GB Cotas: planejador por IA, vínculos, perfil de regras editável | Critério 4.5 com IA; taxa de plano recusado medida | 3 dias |
| 2 | GB Render: passes e manifesto | Critério 5.3 (fase 2) | 5 dias |
| 3 | GB Render: geração e edição pontual (modo A); modo B se necessário | Critério 5.3 (fase 3) | 3 dias |
| 5 | Instalador único (`package-gestaobim.ps1`), `CHANGELOG`, guia do usuário, janela **Sobre** | Instalador instala e remove os dois plugins junto com os outros | 1 dia |

A ordem é **0 → 1 → 4a → 4b → 2 → 3 → 5**. A fase 4a não depende de rede e já entrega valor sozinha.

## 7. Pontos que precisam de decisão

### 7.1 Login por assinatura (OAuth) ou por chave de API: decidido

**Decisão (22/11/2026): os dois, pelo `CLIProxyAPI`, com o risco abaixo aceito pelo usuário.** A janela de
configuração mostra o aviso na primeira vez que alguém conecta uma assinatura, e a chave de API fica sempre disponível
como alternativa, para quem tiver a conta bloqueada ou preferir não arriscar.

O risco, como foi apresentado:

- Os termos dessas assinaturas, em geral, as limitam ao uso nos produtos do próprio fornecedor e por uma pessoa. Um
  plugin distribuído que reaproveita o login delas em outro produto **pode violar esses termos** e levar ao bloqueio
  das contas. Isso precisa ser lido nos termos atuais de cada fornecedor, não presumido.
- O proxy depende de cabeçalhos e endpoints não documentados, que mudam sem aviso (a própria proposta reconhece que
  seria preciso acompanhar mudanças toda semana).
### 7.2 Gateway: decidido

Adotar o `CLIProxyAPI` (consequência de 7.1), com versão travada e o `config.yaml` de 3.2.1.

### 7.3 Demais

- Nomes finais dos plugins e se entram na aba "Gestão BIM" existente (proposta: painel "IA" com os dois botões).
- Distribuição: o CLIProxyAPI é MIT, compatível com a AGPL-3.0. O instalador precisa levar o aviso de licença dele.
- Custo: quem paga as chaves (cada arquiteto ou o escritório) e se o gateway deve impor um teto mensal.
- Privacidade: o render envia imagens do projeto e o manifesto (nomes de família, fabricante) a um provedor externo.
  Isso precisa de um aviso na primeira vez e de uma opção de remover os fabricantes do manifesto.

## 8. Riscos

| Risco | Efeito | Mitigação |
|---|---|---|
| Bordas suavizadas na Color-ID | Manifesto com cores que não existem; objetos errados na edição pontual | Máscara pelo rasterizador próprio (passe 3) |
| Rasterizador próprio lento em modelos grandes | Passe 3 demora minutos | Resolução 1K por padrão, malhas só dos elementos visíveis, detalhe grosso no `CustomExporter` |
| Modelo de imagem desloca a geometria | Render "bonito" mas fora do projeto | Modo B (composição por máscara) e medida de desvio na fase 3 |
| IA devolve plano de cotas inválido | Cotas faltando | Validação por esquema, reenvio único, planejador por regras de reserva |
| `NewDimension` falha em referências de família | Cadeias perdidas | Extrator testado por categoria; falha cancela só a cadeia e entra no relatório |
| Termos de uso das assinaturas (7.1) | Contas bloqueadas | **Risco aceito.** Aviso na primeira conexão; chave de API sempre disponível como alternativa |
| Fornecedor muda o login ou os endpoints da assinatura | Plugin para de responder até a próxima versão do CLIProxyAPI | Erro claro na janela ("assinatura indisponível, use uma chave de API"), troca de versão revisada |
| Padrões inseguros do CLIProxyAPI (3.2.1) | Contas expostas na rede local | `config.yaml` gerado pelo plugin e conferido antes de subir |
| Tokens OAuth em arquivo, sem DPAPI | Outro programa do mesmo usuário lê os tokens | **Risco aceito**; ACL só do usuário, pasta fora do OneDrive |
| Vazamento de chave | Uso indevido e custo | Só em `127.0.0.1`, token por sessão, nunca em log nem em `render_report.json` |

## 9. Execução (0.5.0)

Executado em 22/11/2026, na ordem 0 → 1 → 4a → 4b → 2 → 3 → 5. Guia do usuário e arquitetura em
[GB-COTAS-E-RENDER.md](GB-COTAS-E-RENDER.md).

### 9.1 O que mudou em relação ao plano

- **Clay também sai do rasterizador**, não do `ExportImage`. Os quatro passes vêm do mesmo z-buffer e por isso ficam
  alinhados pixel a pixel, sem borda suavizada. O `ExportImage` só aparece no autoteste, para conferir o enquadramento
  da câmera.
- **Câmera da perspectiva:** a janela sai do **contorno do recorte** (`GetCropRegionShapeManager().GetCropShape()`),
  em coordenadas do modelo, visto do olho. O contorno fica sempre a 0,1 pé do olho. O `Max.Z` da CropBox é o plano de
  corte próximo, não o de projeção: usar o `Max.Z` deixava a imagem 27% "mais perto" que a do Revit. Uma **perspectiva
  sem recorte** é recusada, porque o Revit mostra um enquadramento próprio que a API não informa. O autoteste
  confirmou isso comparando com o `ExportImage`.
- **O preview das cotas é uma lista com caixas de marcar**, não `DirectContext3D`. Nada vai para o modelo antes de
  "Criar cotas", e tudo o que vai cabe num Ctrl+Z.
- **Planta girada:** o extrator usa a direção dominante das paredes como base, e não os eixos da vista. A tolerância
  de paralelismo é estrita (2·10⁻⁶ rad): com 1,8°, paredes quase paralelas entravam na mesma cadeia e o Revit criava
  cotas inválidas (valor −1 pé). Depois de criar, cada cota é regenerada e lida de volta. Se ficar inválida ou o
  desvio passar de 50 mm, a cadeia é desfeita.
- **Referências de abertura:** as referências Left, Right e CenterLeftRight de portas e janelas são opacas, sem
  posição. A posição de cada uma é medida com uma cota descartável numa transação desfeita.
- Ainda não feito: miniaturas do catálogo do GB Famílias como referência no render (os swatches vêm do material), cota
  pelo núcleo da parede (`UseWallCore` está no perfil, mas o extrator ainda usa só as faces de acabamento), vínculos
  (fase 4b), **cortes e elevações** (a cota vertical precisa de níveis e lajes; o autoteste mostrou que o extrator
  de plantas trata as paredes cortadas como inclinadas, então a vista é recusada com o motivo) e o modo B do render
  (ComfyUI).

### 9.2 Medido

| Item | Resultado |
|---|---|
| Testes sem Revit | `GBAi.Shared.Tests` 63, `GBCotas.Core.Tests` 50, `GBRender.Core.Tests` 68: todos passam. Os 304 dos outros três plugins continuam passando |
| Gateway real (CLIProxyAPI 8.1.1) | 401 sem token, 200 com token, 413 acima de 64 MB, recusado no IP da rede local, gerenciamento só com a chave, painel 404, ACL do `auth-dir`, dois gateways ao mesmo tempo, exe com outro SHA-256 recusado |
| GB Cotas no Revit 2025 (`revit-selftest-ia.ps1 -Plugin Cotas`, pelas regras) | m03, m04, m06 e m07: **8 plantas, 54 cadeias, todas criadas como `Dimension` nativa, 0 colisões AABB, valores do Revit iguais aos do plano (desvio ≤ 2 mm)**, inclusive a planta de locação girada 32,43°. m05 (quase só parede-cortina) e os cortes de cada projeto: recusados com o motivo |
| GB Render no Revit 2025 (`-Plugin Render`) | m03, m04, m06 e m07: **4 camadas do mesmo tamanho e 100% das cores da máscara no manifesto** em 6 vistas (330 mil a 930 mil triângulos, extração em menos de 2 s). Perspectiva com recorte (m07): mesma proporção do `ExportImage`, e o enquadramento bate a olho (a comparação automática fica inconclusiva com céu ao fundo). Perspectivas sem recorte (m04, m06): recusadas com o motivo |
| Instalador | `GestaoBIM-Setup-0.5.0.exe` com os cinco plugins para 2025, 2026 e 2027, o CLIProxyAPI conferido por SHA-256, `THIRD-PARTY.txt` e a licença MIT dele |

### 9.3 Não verificado

- **Geração real com IA** (render, edição pontual e planejamento de cotas por IA). Faltam uma chave ou uma conta
  conectada. O caminho está coberto por testes com um gateway falso (formatos de resposta, recusa e reenvio do plano,
  conferência da edição) e pelo gateway real sem provedor. O primeiro uso com uma chave de verdade é o teste que falta.
- Login por assinatura (OAuth) de ponta a ponta: o plugin chama o CLIProxyAPI com a flag de login certa (testado), mas
  o fluxo no navegador não foi feito.
- Autotestes no Revit 2026 e 2027: as DLLs compilam para os dois. O autoteste só rodou no 2025, porque os dois estavam
  abertos com projetos do usuário.
- Janelas (cliques) do GB Cotas, do GB Render e do Configurar IA: testadas só pela lógica que elas chamam.
