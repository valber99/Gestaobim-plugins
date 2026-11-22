# Arquitetura e decisões

O produto é um **add-in nativo do Revit (C#/.NET)**. A edição em pyRevit/Python ficou como legado (última
seção).

## Visão geral

```
src/GBMaterialStudio.Core   regras puras e banco de favoritos (sem Revit), testado em Windows e Linux
src/GBMaterialStudio        o add-in: comandos, janelas WPF/XAML e a camada que usa a API do Revit
tests/GBMaterialStudio.Core.Tests   testes xUnit do Core
installer, build            manifesto .addin, Install.ps1/Uninstall.ps1, empacotamento (zip e .exe)
```

Um código-fonte, três compilações (`-p:RevitVersion=2025|2026|2027`): `net8.0-windows` para o Revit 2025 e
2026 e `net10.0-windows` para o 2027. A API vem do NuGet (`Nice3point.Revit.Api.RevitAPI` e `RevitAPIUI`), então
não é preciso ter o Revit para compilar. O Core é compilado dentro da mesma DLL do add-in
(`GBMaterialStudio.dll`), então o usuário recebe uma DLL por versão do Revit.

### Mapa do add-in (`src/GBMaterialStudio`)

| Pasta / arquivo | Papel |
|---|---|
| `Addin/App.cs` | `IExternalApplication`: cria a aba "Gestão BIM", o painel "Materiais" e os seis botões |
| `Addin/Commands.cs` | Um `IExternalCommand` por botão, todos via `CommandRunner` (captura erros em vez de derrubar o Revit) |
| `Addin/Flows.cs` | Fluxos: editar material (modal e fixo), favoritos, organizar, aplicar em tipo |
| `Addin/UpdateNotifier.cs` | Consulta de versão nova deste plugin e a sua seção na janela única **Sobre** (`src/GBShared/AboutHub.cs`, compartilhado com os outros plugins) |
| `Revit/MaterialServices.cs`, `AppearanceSchemas.cs` | Leitura e escrita de materiais, aparência, texturas e padrões |
| `Revit/Hygiene.cs` | Uso real de cada material, assinatura, exclusão segura, renomeação em duas fases |
| `Revit/MaterialLibrary.cs`, `Favorites.cs` | Biblioteca portátil (`.rvt` por versão) e favoritos sobre o banco |
| `Revit/TypeLayers.cs` | Material por camada em tipos compostos |
| `Revit/FacePicker.cs`, `CursorHook.cs` | Seleção de face e cursor do conta-gotas sem piscar |
| `Revit/RevitDispatcher.cs` | Janelas que não bloqueiam o Revit (`ExternalEvent`) |
| `Revit/Notifications.cs`, `Context.cs` | Avisos do Windows e sessão atual do Revit |
| `UI/*.xaml` e code-behind | Editor de material, editor de textura, favoritos e organizar |
| `UI/Dialogs.cs`, `Visuals.cs` | Diálogos e elementos visuais compartilhados |

### Mapa do Core (`src/GBMaterialStudio.Core`)

| Arquivo | Papel |
|---|---|
| `Renaming.cs` | Regras de renomear em lote (`RenameRules`, `Renamer`), agrupamento de duplicados |
| `Storage.cs` | Pastas de dados, `Settings` e `FavoriteStore`: leitura que não perde dados, escrita atômica, backup, mutex nomeado |
| `Updates.cs` | `UpdateRules` (ler a tag, comparar versões, interpretar a resposta do GitHub) e `UpdateState` (última consulta, versão ignorada) |

## Decisões

### Add-in nativo em vez de extensão pyRevit

A edição em pyRevit exigia o pyRevit instalado e dependia do motor Python dele, com várias limitações do
CPython sob pythonnet (ver a seção legada). Um add-in nativo não depende de nada além do Revit e é mais fácil de
distribuir (instalador `.exe`). O custo é compilar, e testar a camada do Revit só dentro do Revit. Por isso as
regras que não precisam do Revit ficam no Core, com testes.

### Uma DLL por versão do Revit

O Revit 2027 roda em .NET 10 e o 2025/2026 em .NET 8. O mesmo código compila para os dois alvos. As pastas
`2025`, `2026` e `2027` do pacote têm cada uma a sua DLL, o `eyedropper.cur` e o `gestaobim.png`. O manifesto
`.addin` tem um `AddInId` fixo e a classe `Gestaobim.MaterialStudio.Addin.App`.

### Janelas que não bloqueiam o Revit

Um manipulador de uma janela modeless roda fora de um contexto de API do Revit e não pode alterar o modelo.
Os manipuladores que tocam o modelo passam pelo `RevitDispatcher`, que os enfileira num `ExternalEvent`. Os que
só mexem na interface rodam direto. **Editar material** é modal; **Editor fixo** é a mesma janela sem bloquear.

### Aplicar é tudo ou nada

Alterações de material usam uma `Transaction` com `AppearanceAssetEditScope`. Se algo falha, a transação é
revertida e nada muda. Para o usuário, aplicar é uma entrada só de desfazer.

### Excluir não usados

`GetMaterialIds` não enxerga materiais referenciados por parâmetros de material de famílias (por exemplo
`BOS_*_Material`). Por isso a varredura própria lê também os parâmetros, e o critério final exige concordância
com o `GetUnusedElements` do Revit, que é mais rigoroso. A exclusão roda em subtransações e é desfeita se
removeria algo além do material.

### Biblioteca portátil como um projeto do Revit

A alternativa seria guardar as propriedades em JSON e recriar o material, o que perde fidelidade (esquemas de
aparência, mapas, parâmetros). Usar um `.rvt` de biblioteca e o `CopyElements` nativo leva o material completo. O
custo é abrir esse arquivo por alguns segundos ao favoritar ou transferir. Há um arquivo por versão do Revit,
porque um `.rvt` salvo numa versão não abre em versão anterior. Um favorito guarda o `library_uid` do material
dentro da biblioteca; se a origem estiver aberta, a cópia vem dela (mais atual), senão vem da biblioteca.

### Identidade do projeto de origem

A chave do favorito usa o `UniqueId` do `ProjectInformation` (`project_id`), e não o caminho do arquivo, então
mover ou renomear o arquivo não perde os favoritos. Os favoritos antigos continuam funcionando pela chave com
caminho. Limitação: duas cópias de um mesmo projeto salvas com nomes diferentes compartilham o mesmo id; o
`material_unique_id` continua distinguindo o material.

### Reconhecer um favorito já transferido

Depois de transferido, o material do projeto tem outro `UniqueId`. Cada favorito guarda uma `signature` (SHA-1
de cor, transparência, padrões, esquema de aparência e texturas). Se o projeto já tem um material com o mesmo
nome e a mesma assinatura, ele é reutilizado. Com o mesmo nome e conteúdo diferente, a transferência é recusada.

### Banco de favoritos

JSON com leitura que falha fechada (um arquivo ilegível nunca é sobrescrito), escrita atômica, backups e um
mutex nomeado entre processos do Revit. Detalhe do `System.Text.Json`: inteiros criados em memória não passam
por `TryGetValue<long>`; o código usa `TryGetInteger`.

### Migração da edição pyRevit

Na primeira abertura, `StoragePaths.MigrateFromPyRevit` copia favoritos, configurações e biblioteca de
`%APPDATA%\pyRevit\MaterialTools` para `%APPDATA%\GestaoBIM\MaterialStudio`, sem alterar a pasta antiga. O
`Install.ps1` move as extensões antigas do pyRevit para backup, para a aba não duplicar.

### Aviso de atualização

`UpdateNotifier` consulta `releases/latest` da API do GitHub, em segundo plano e no máximo uma vez por dia
(`update-check.json`). Se a versão for mais nova, a bolinha vermelha é aplicada no botão **Sobre** no evento
`Idling`, porque a faixa só pode ser alterada na thread de interface do Revit. Só são aceitos lançamentos
oficiais (nem rascunho nem pré-lançamento) com link do `github.com`. Qualquer falha de rede é silenciosa. Não há
atualização automática: trocar a DLL com o Revit aberto não é possível, então o botão leva à página de download.
`GB_MATERIAL_STUDIO_NO_UPDATE_CHECK=1` desliga a consulta automática.

### Instalador `.exe`

Gerado pelos scripts de `build/` (`New-LargeSetupExe` em `common.ps1`), igual para os três plugins. É o
`build/SetupStub.cs`, compilado pelo `csc` do .NET Framework que já vem no Windows (sem dependências), com o `.zip`
colado no fim e um rodapé com o nome do log, o SHA-256 e o tamanho do pacote. Ao rodar, ele:
1. recusa instalar com o Revit aberto;
2. confere o SHA-256 (um download corrompido não instala);
3. extrai para `%TEMP%` e roda o `Install.ps1`.

Não pede administrador: instala no perfil do usuário. `/quiet` não pausa no fim (usado nos testes automáticos).
O log fica em `%TEMP%\<plugin>-setup.log`, por exemplo `gb-material-studio-setup.log`.

Até a 0.3.1 o instalador era do IExpress, que travava com o pacote de ~0,7 GB do GB Famílias e não roda sem um
clique na confirmação do Windows.

## Testes

- `dotnet test tests/GBMaterialStudio.Core.Tests`: 51 testes (renomeação, duplicados, banco de favoritos,
  configurações, regras de atualização).
- A camada do Revit não tem testes automáticos. Foi verificada dentro do Revit 2027 (carregamento, faixa,
  análise de materiais, botão Sobre). Revit 2025 e 2026 compilam, mas não foram abertos.

---

# Edição legada em pyRevit

Mantida em `GBMaterialStudio.extension` só como referência. Exige pyRevit 5+ (motor CPython 3.12).

### Mapa dos módulos (`GBMaterialStudio.extension/lib`)

| Módulo | Papel | Depende do Revit |
|---|---|---|
| `mt_bootstrap` | Carrega os módulos na ordem certa a cada clique | não |
| `mt_hygiene_core` | Regras de renomear em lote e agrupamento de duplicados | não (testado com pytest) |
| `mt_settings` | Configurações em JSON (`gb-settings.json`) | não (testado) |
| `mt_favorite_store` | Banco de favoritos: leitura que não perde dados, escrita atômica, backup, bloqueio | não (testado) |
| `mt_services` | Leitura e escrita de materiais, aparência, texturas, padrões e pintura | sim |
| `mt_hygiene` | Uso real de cada material, assinatura, exclusão segura, renomeação em duas fases | sim |
| `mt_library` | Biblioteca portátil e cópia nativa de materiais entre documentos | sim |
| `mt_favorites`, `mt_favorites_legacy` | Favoritos sobre o banco, com a ponte para a biblioteca | sim |
| `mt_types` | Material por camada em tipos compostos | sim |
| `mt_editor`, `mt_texture_editor`, `mt_favorites_browser`, `mt_organizer` | Janelas WPF | sim |
| `mt_modeless` | Janela que não bloqueia o Revit (ExternalEvent) | sim |
| `mt_wpf`, `mt_dialogs` | Janelas WPF e diálogos próprios (o `pyrevit.forms` não existe no CPython) | sim |
| `mt_cursor` + `GBCursorHook.dll` | Cursor do conta-gotas sem piscar | sim |

### Decisões da edição pyRevit

#### Python 3 (CPython) em vez de IronPython 2.7

O IronPython 2.7 está em fim de vida, e o pyRevit 5+ traz um motor CPython 3.12. A extensão foi migrada
para ele (`#! python3` em todo botão). O motor CPython tem limitações que moldaram o código:

- **`pyrevit.forms` não é suportado.** Qualquer chamada lança `PyRevitCPythonNotSupported`. Por isso
  existem `mt_wpf` (carrega XAML com `XamlReader` e expõe os controles pelo `x:Name`, como o antigo
  `WPFWindow`) e `mt_dialogs` (alerta, texto, arquivo, cor e lista com filtro).
- **Binding do WPF com objetos Python.** Uma lista Python é recusada por `ItemsSource`; é preciso uma
  `List[System.Object]` (`mt_wpf.net_list`). Texto liga direto a atributos Python; um **pincel** só liga
  se a classe tiver propriedade .NET real (`clr.clrproperty`), e é por isso que `PatternItem` e o item do
  navegador de favoritos são tipos .NET (`mt_wpf.clr_property`). `SelectedItem` devolve o mesmo objeto Python.
- **Interfaces do Revit** (`ISelectionFilter`, `IExternalEventHandler`, `IDuplicateTypeNamesHandler`) só
  podem ser implementadas com `__namespace__`. Cada carga do módulo usa um namespace novo
  (`mt_wpf.NAMESPACE`), então recarregar a extensão a cada clique nunca colide com um tipo antigo.
- **DLL do cursor.** Uma classe sem namespace não é alcançável pelo `import` do pythonnet. O C# agora vive em
  `GestaoBim.Native.CursorHook` (`GBCursorHook.dll`), carregado com `Assembly.LoadFrom`. O nome mudou também
  porque o Revit mantém a DLL antiga carregada até reiniciar.
- **`imp` não existe no Python 3.12.** `mt_bootstrap` recarrega os módulos com `importlib`.
- **Sem `exitscript`/`sys.exit`.** Os botões são uma função `main()` que simplesmente retorna.

O banco de favoritos usa o mutex nomeado do .NET dentro do Revit (o Windows o libera se um escritor
travar) e um arquivo de trava cooperativo quando o .NET não está disponível, como nos testes.

Os módulos puros (`mt_hygiene_core`, `mt_settings`, `mt_favorite_store`) são testados em CPython 3.12,
a mesma versão do Revit. `tests/test_cpython_rules.py` impede a volta de `pyrevit.forms`, `imp` e
construções do Python 2.
#### Biblioteca portátil como um projeto do Revit

A alternativa seria guardar as propriedades do material em JSON e recriá-lo. Isso perde fidelidade
(esquemas de aparência, mapas, parâmetros). Usar um `.rvt` de biblioteca e o `CopyElements` nativo
leva o material completo. O custo é abrir esse arquivo por alguns segundos ao favoritar ou transferir.
Há um arquivo por versão do Revit, porque um `.rvt` salvo numa versão não abre em versão anterior.

Um favorito guarda o `library_uid` do material dentro da biblioteca. Se a origem estiver aberta, a cópia
vem dela (é a mais atual); senão, vem da biblioteca.

#### Identidade do projeto de origem

Antes, a chave do favorito era `id do projeto | caminho do arquivo`, então mover ou renomear o arquivo
"perdia" os favoritos. Os novos usam só o `UniqueId` do `ProjectInformation` (`project_id`). Os antigos
continuam funcionando pela chave com caminho. Limitação: duas cópias de um mesmo projeto salvas com
nomes diferentes compartilham o mesmo id; o `material_unique_id` continua distinguindo o material.

#### Reconhecer um favorito já transferido

Depois de transferido, o material do projeto tem outro `UniqueId`. Cada favorito guarda uma
`signature` (hash de cor, transparência, padrões, esquema de aparência e texturas). Se o projeto já tem
um material com o mesmo nome e a mesma assinatura, ele é reutilizado em vez de falhar com "já existe".
Com o mesmo nome e conteúdo diferente, a transferência continua sendo recusada.

#### Excluir não usados

`GetMaterialIds` não enxerga materiais referenciados por parâmetros de material de famílias (por
exemplo `BOS_*_Material`). Por isso a varredura própria lê também os parâmetros, e o critério final
exige concordância com o `GetUnusedElements` do Revit, que é mais rigoroso. A exclusão roda em
subtransações e é desfeita se removeria algo além do material.

#### Janela fixa (modeless)

Um manipulador de uma janela modeless roda fora de um contexto de API do Revit e não pode alterar o
modelo. Os manipuladores que tocam o modelo são envolvidos por `in_revit`, que os enfileira num
`ExternalEvent`. Os que só mexem na interface (sliders, pré-visualizações) rodam direto. O botão
**Editor fixo** mantém a janela e os manipuladores vivos depois que o script termina (no CPython não é
preciso nenhum marcador de motor persistente; a janela fica referenciada em `mt_modeless`). O botão
**Editar material** continua modal e é o caminho mais simples.

#### Compatibilidade com a extensão antiga

`GBMaterialStudio.extension` substitui `ValberMaterialStudio.extension` (e a antiga
`MaterialTools.extension` da suite). O banco de favoritos (`favorites-v4.json`) e o legado
(`favorites.json`) continuam no mesmo lugar e são lidos sem alteração.
