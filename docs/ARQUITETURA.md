# Arquitetura e decisões

## Mapa dos módulos (`GBMaterialStudio.extension/lib`)

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

## Decisões

### Python 3 (CPython) em vez de IronPython 2.7

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
### Biblioteca portátil como um projeto do Revit

A alternativa seria guardar as propriedades do material em JSON e recriá-lo. Isso perde fidelidade
(esquemas de aparência, mapas, parâmetros). Usar um `.rvt` de biblioteca e o `CopyElements` nativo
leva o material completo. O custo é abrir esse arquivo por alguns segundos ao favoritar ou transferir.
Há um arquivo por versão do Revit, porque um `.rvt` salvo numa versão não abre em versão anterior.

Um favorito guarda o `library_uid` do material dentro da biblioteca. Se a origem estiver aberta, a cópia
vem dela (é a mais atual); senão, vem da biblioteca.

### Identidade do projeto de origem

Antes, a chave do favorito era `id do projeto | caminho do arquivo`, então mover ou renomear o arquivo
"perdia" os favoritos. Os novos usam só o `UniqueId` do `ProjectInformation` (`project_id`). Os antigos
continuam funcionando pela chave com caminho. Limitação: duas cópias de um mesmo projeto salvas com
nomes diferentes compartilham o mesmo id; o `material_unique_id` continua distinguindo o material.

### Reconhecer um favorito já transferido

Depois de transferido, o material do projeto tem outro `UniqueId`. Cada favorito guarda uma
`signature` (hash de cor, transparência, padrões, esquema de aparência e texturas). Se o projeto já tem
um material com o mesmo nome e a mesma assinatura, ele é reutilizado em vez de falhar com "já existe".
Com o mesmo nome e conteúdo diferente, a transferência continua sendo recusada.

### Excluir não usados

`GetMaterialIds` não enxerga materiais referenciados por parâmetros de material de famílias (por
exemplo `BOS_*_Material`). Por isso a varredura própria lê também os parâmetros, e o critério final
exige concordância com o `GetUnusedElements` do Revit, que é mais rigoroso. A exclusão roda em
subtransações e é desfeita se removeria algo além do material.

### Janela fixa (modeless)

Um manipulador de uma janela modeless roda fora de um contexto de API do Revit e não pode alterar o
modelo. Os manipuladores que tocam o modelo são envolvidos por `in_revit`, que os enfileira num
`ExternalEvent`. Os que só mexem na interface (sliders, pré-visualizações) rodam direto. O botão
**Editor fixo** mantém a janela e os manipuladores vivos depois que o script termina (no CPython não é
preciso nenhum marcador de motor persistente; a janela fica referenciada em `mt_modeless`). O botão
**Editar material** continua modal e é o caminho mais simples.

### Compatibilidade com a extensão antiga

`GBMaterialStudio.extension` substitui `ValberMaterialStudio.extension` (e a antiga
`MaterialTools.extension` da suite). O banco de favoritos (`favorites-v4.json`) e o legado
(`favorites.json`) continuam no mesmo lugar e são lidos sem alteração.
