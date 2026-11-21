# Gestaobim-plugins

Plugins de [pyRevit](https://github.com/pyrevitlabs/pyRevit) do ecossistema **Gestão BIM**.

Hoje o repositório tem uma extensão: **GBMaterialStudio**, um estúdio de materiais dentro do Revit.
Ele aparece na faixa de opções, na aba **Gestão BIM**, painel **Materiais**.

## O que a GBMaterialStudio faz

| Botão | Para que serve |
|---|---|
| **Editar material** | Aponte uma face e edite, numa janela só, cor, padrões de superfície e corte, aparência de render, texturas e identidade do material. Aplicar é um único Ctrl+Z, e é tudo ou nada. |
| **Editor fixo** | O mesmo editor numa janela que fica ao lado do Revit, sem bloquear: continue navegando e selecionando enquanto edita. |
| **Favoritos** | Biblioteca de materiais. Pesquise, abra no editor ou pinte faces direto com um favorito. |
| **Organizar materiais** | Veja onde cada material é usado, encontre duplicados, exclua os não usados e renomeie em lote. |
| **Aplicar em tipo** | Troque o material de camadas de tipos de parede, piso, cobertura e forro, usando a seleção atual ou uma lista. O destino pode ser um material do projeto ou um favorito. |

### Biblioteca portátil

Ao favoritar um material, ele é copiado para uma biblioteca (um arquivo `.rvt` por versão do Revit,
`GBMaterialLibrary-<versão>.rvt`). Isso permite levar o material, completo e com a aparência, para
qualquer projeto sem precisar abrir o projeto de onde ele veio.

- Pasta padrão: `%APPDATA%\pyRevit\MaterialTools`.
- Para usar outra pasta (por exemplo, uma pasta de rede compartilhada), crie
  `%APPDATA%\pyRevit\MaterialTools\gb-settings.json` com `{"library_dir": "D:\\Biblioteca"}`.
- As texturas continuam sendo arquivos no disco: o caminho precisa existir na máquina de destino.
  A transferência é cancelada, sem alterar o projeto, se alguma textura não for encontrada.

### Excluir não usados com segurança

Um material só é tratado como "não usado" quando duas verificações concordam: a varredura própria
(elementos, tipos, parâmetros de material de famílias e subcategorias) e o `GetUnusedElements` do
próprio Revit, o mesmo critério do Purge Unused. Cada exclusão é desfeita se o Revit indicar que ela
removeria outros elementos. Tudo é uma entrada só de desfazer.

## Requisitos

- Windows e Revit 2025 ou mais novo (o *Organizar materiais* usa uma API do Revit 2024+).
- pyRevit 4.8 ou mais novo.

## Instalação

```powershell
git clone https://github.com/valber99/Gestaobim-plugins.git
cd Gestaobim-plugins
.\scripts\install.ps1
```

O script copia `GBMaterialStudio.extension` para `%APPDATA%\pyRevit\Extensions`, guarda um backup do que
já estava lá e, com `-RemoveLegacy`, remove a versão antiga `ValberMaterialStudio.extension`. Depois,
recarregue o pyRevit (aba pyRevit, Reload) ou reinicie o Revit.

Instalação manual: copie a pasta `GBMaterialStudio.extension` para `%APPDATA%\pyRevit\Extensions`,
ou adicione a pasta do clone como caminho de extensões do pyRevit.

## Desenvolvimento

O código roda dentro do Revit, em **IronPython 2.7** (limitação do pyRevit): sem f-strings, sem type
hints, sem bibliotecas que dependam de CPython 3. Para HTTP use `urllib`/`urllib2`; para interface,
WPF/XAML.

```powershell
python -m pip install pytest
python -m pytest -q
```

Os testes cobrem o que não depende do Revit: as regras de renomear e de duplicados, as configurações,
o banco de favoritos (leitura, escrita atômica, backup, bloqueio) e um guarda que reprova sintaxe que o
IronPython 2.7 não aceita. O que depende da API do Revit é verificado dentro do Revit.

Arquivos gerados por script (rode de novo se alterar a origem):

Em `GBMaterialStudio.extension/`:

- `tools/build_native.ps1` compila `lib/GestaoBimCursorHook.dll` a partir de `tools/GestaoBimCursorHook.cs`.
  Precisa do `csc.exe` do .NET Framework (já vem no Windows).
- `tools/build_cursor.ps1` gera `lib/eyedropper.cur` (cursor do conta-gotas) a partir de `lib/gestaobim.png`.
- `tools/build_icons.ps1` gera os ícones dos botões novos.

Mais detalhes de arquitetura e das decisões em [`docs/ARQUITETURA.md`](docs/ARQUITETURA.md).

## Licença

[GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0).
