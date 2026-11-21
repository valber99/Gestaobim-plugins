# Gestaobim-plugins

Plugins do ecossistema **Gestão BIM** para o Revit.

Hoje o repositório tem o **GB Material Studio**, um estúdio de materiais dentro do Revit. Ele é um
**add-in nativo (.NET)**: não precisa de pyRevit e não precisa de Python instalado.

## O que o GB Material Studio faz

Tudo fica na aba **Gestão BIM**, painel **Materiais**.

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

- Pasta padrão: `%APPDATA%\GestaoBIM\MaterialStudio`.
- Para usar outra pasta (por exemplo, uma pasta de rede compartilhada), crie
  `%APPDATA%\GestaoBIM\MaterialStudio\gb-settings.json` com `{"library_dir": "D:\\Biblioteca"}`.
- As texturas continuam sendo arquivos no disco: o caminho precisa existir na máquina de destino.
  Se alguma não for encontrada, o material é transferido e você recebe um aviso.
- Quem usava a edição em pyRevit não perde nada: favoritos, configurações e a biblioteca de
  `%APPDATA%\pyRevit\MaterialTools` são copiados na primeira abertura (a pasta antiga não é alterada).

### Excluir não usados com segurança

Um material só é tratado como "não usado" quando duas verificações concordam: a varredura própria
(elementos, tipos, parâmetros de material de famílias e subcategorias) e o `GetUnusedElements` do
próprio Revit, o mesmo critério do Purge Unused. Cada exclusão é desfeita se o Revit indicar que ela
removeria outros elementos. Tudo é uma entrada só de desfazer.

## Instalação

1. Baixe da página de [Releases](https://github.com/valber99/Gestaobim-plugins/releases) o instalador
   **`GBMaterialStudio-Setup-<versão>.exe`** (ou o `.zip`, se preferir instalar pelo `Instalar.bat`).
2. Feche o Revit e dê dois cliques no `.exe` (ele recusa instalar com o Revit aberto). O Windows pode avisar que o
   editor é desconhecido, porque o instalador não é assinado: clique em "Mais informações" e "Executar assim mesmo".
3. Abra o Revit. Na primeira vez ele pergunta se confia no add-in (não há assinatura digital ainda):
   escolha **"Sempre carregar"**.

Quando sai uma versão nova, o botão **Sobre** ganha uma bolinha vermelha (uma consulta por dia ao GitHub, em
segundo plano; clicar no botão consulta na hora e leva à página de download. Para desligar a consulta automática, defina a variável de ambiente `GB_MATERIAL_STUDIO_NO_UPDATE_CHECK=1`).

Para remover, `Desinstalar.bat`. Seus favoritos e a biblioteca não são apagados.

Requisitos: Windows e **Revit 2025, 2026 ou 2027**.

## Desenvolvimento

Um código-fonte, três compilações (uma por ano do Revit), com a API vinda do NuGet
(`Nice3point.Revit.Api`), então não é preciso ter o Revit instalado para compilar.

```powershell
dotnet test tests/GBMaterialStudio.Core.Tests        # regras puras, configurações e banco de favoritos
dotnet build src/GBMaterialStudio -c Release -p:RevitVersion=2027
./build/package.ps1                                  # gera o .zip e o instalador .exe em dist/ (2025, 2026, 2027)
```

Estrutura:

- `src/GBMaterialStudio.Core` — regras puras e o banco de favoritos (sem Revit; testado em Windows e Linux).
- `src/GBMaterialStudio` — o add-in: comandos, janelas (WPF/XAML) e a camada que usa a API do Revit.
- `tests/GBMaterialStudio.Core.Tests` — testes xUnit.
- `installer` e `build` — manifesto `.addin`, instaladores e empacotamento.
- `GBMaterialStudio.extension` — **edição legada em pyRevit** (mantida só como referência, ver abaixo).

Documentação:

- [`docs/GUIA-DO-USUARIO.md`](docs/GUIA-DO-USUARIO.md): instalar, usar cada botão, onde ficam os dados, problemas comuns.
- [`docs/ARQUITETURA.md`](docs/ARQUITETURA.md): como o add-in é organizado e por que cada decisão foi tomada.
- [`docs/RELEASE.md`](docs/RELEASE.md): como gerar e publicar uma versão.
- [`CHANGELOG.md`](CHANGELOG.md): histórico de versões.

### Edição legada em pyRevit

Antes do add-in nativo, o plugin existia como extensão do pyRevit (`GBMaterialStudio.extension`, Python 3).
Ela continua no repositório, com testes (`python -m pytest -q`), mas **não é mais a edição recomendada**:
exige pyRevit 5+. Se as duas estiverem instaladas, a aba "Gestão BIM" aparece duplicada; o
`Instalar.bat` move a edição pyRevit para fora do caminho automaticamente.

## Licença

[GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0).
