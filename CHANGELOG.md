# Changelog

As versões do **GB DWG Export** têm tag `dwg-v<versão>` e estão listadas aqui com o prefixo "DWG".
As do **GB Famílias** têm tag `familias-v<versão>` e o prefixo "Famílias".
As do GB Material Studio têm tag `v<versão>`.

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
- Os modelos de IA pesam ~1 GB. Confira os direitos de redistribuição das famílias.

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
