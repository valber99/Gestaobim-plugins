# GB DWG Export

Plugin do Revit (2025, 2026 e 2027) que exporta folhas e vistas para DWG/DXF e junta várias folhas num só desenho,
**sem precisar do AutoCAD**. Faz parte do Gestão BIM e usa o mesmo instalador e a mesma aba do GB Material Studio.

## Guia do usuário

### Instalar

1. Baixe `GBDwgExport-Setup-<versão>.exe` na página de [Releases](https://github.com/valber99/Gestaobim-plugins/releases)
   (as versões deste plugin têm a tag `dwg-v<versão>`).
2. Feche o Revit e dê dois cliques no `.exe`. Ele recusa instalar com o Revit aberto.
3. O Windows pode avisar que o editor é desconhecido (o instalador não é assinado): "Mais informações" e
   "Executar assim mesmo".
4. Abra o Revit. Na primeira vez ele pergunta se confia no add-in: escolha **"Sempre carregar"**.

O pacote traz o `cadcraft-cli` (o motor de mesclagem, 67 MB) e o instala em `%APPDATA%\GestaoBIM\Tools\cadcraft`. Não é
preciso instalar o CADCraft nem o AutoCAD. Também há um `.zip` com `Instalar.bat` e `Desinstalar.bat`.

### Onde fica

Aba **Gestão BIM**, painel **Exportar DWG**:

| Botão | O que faz |
|---|---|
| **Exportar folhas** | Um arquivo DWG ou DXF por folha, exatamente como o Revit exporta. |
| **Múltiplos layouts** | Um só DWG, com um layout (e uma janela de visualização) por folha. |
| **Model space** | Um só desenho com as folhas lado a lado no model space. |
| **Layout único** | Um só desenho com um único layout que mostra todas as folhas. |
| **Exportar vistas** | Plantas, cortes, elevações, 3D e outras vistas (não só folhas), um arquivo por vista. |
| **Exportar / Importar configurações** | Salva as configurações num `.zip` e lê um `.zip` de outra máquina. |
| **Sobre** | Mostra a versão e consulta o GitHub; uma bolinha vermelha avisa que há versão nova. |

### A janela de exportação

- **Lista:** folhas (ou vistas) em ordem natural (A2 vem antes de A10), com busca por número, nome ou tipo.
  **Marcar todas** marca as que estão visíveis na busca.
- **Resultado:** um arquivo por folha, ou um dos três modos de desenho único (os botões do painel só escolhem o modo
  inicial; dá para trocar na janela). Para vistas, só "um arquivo por vista" faz sentido.
- **Arquivo:** DWG ou DXF; nome "Número - Nome da folha" ou só o número; e a **configuração de exportação do Revit**
  (as de *Exportar > Opções > Configuração de DWG/DXF* do projeto). Sem escolher, o plugin usa padrões: vistas
  mescladas, planos de referência e caixas de corte ocultos, cores indexadas, sólidos como malha e a unidade do projeto.
- **Papel dos layouts:** A0 a A4, paisagem ou retrato (nos modos com layout).
- **Guardar também os arquivos individuais:** nos modos de desenho único, os DWG exportados pelo Revit ficam numa
  pasta `<projeto> - individuais`, ao lado do desenho mesclado. Vêm direto do Revit e não passam pela mesclagem.
- **Pasta de destino** e **Abrir a pasta ao terminar**. As escolhas ficam salvas em `%APPDATA%\GestaoBIM\DwgExport`.

### Nomes de arquivos e erros

- Acentos são removidos e `\ / : * ? " < > |` viram `_`. Nome vazio vira "Export". Nomes repetidos ganham `_2`, `_3`.
- O caminho completo é conferido **antes** de exportar. Se passar de 259 caracteres, o plugin oferece a Área de
  Trabalho, numa pasta com o nome do projeto; se ainda ficar longo, lista os arquivos e pede para encurtar.
- Arquivo que já existe: pergunta "Deseja sobrescrever?".
- Uma folha que falha não derruba as outras: o resumo no fim lista as falhas.
- O resumo também mostra **o que a mesclagem não conseguiu manter**, quando o motor avisa (ver abaixo).

### Sobre a mesclagem e o que ela pode perder

A mesclagem lê os DWG exportados pelo Revit e grava um novo DWG com o `cadcraft-cli`. O CADCraft é um projeto jovem
(versão 0.20, em fase alfa), e **ele mesmo documenta que salvar DWG descarta o que ele não modela** e que o DWG
gerado nunca foi conferido no AutoCAD. Nos testes deste plugin, DWG e DXF reais do Revit mantiveram o mesmo número de
entidades, camadas, blocos e layouts; a única perda avisada foi de cotas em arco (`ARC_DIMENSION`) num DWG de 20 MB.
Por isso:

- o resumo mostra as perdas que o motor avisa;
- os arquivos individuais (que não passam pelo motor) são guardados por padrão;
- **abra o resultado no AutoCAD ou no DWG TrueView e confira antes de entregar ao cliente.**

Fontes SHX não são lidas pelo motor, e entidades que ele não conhece podem ser descartadas sem aviso.

## Arquitetura

```
src/GBDwgExport.Core   regras puras, sem Revit (testadas em Windows e Linux)
src/GBDwgExport        o add-in: faixa, comandos, janela WPF, exportação e ponte com o motor
tests/GBDwgExport.Core.Tests   testes xUnit do Core
build/package-dwg.ps1  compila 2025/2026/2027, embute o cadcraft-cli e gera .zip e .exe
```

| Arquivo | Papel |
|---|---|
| `Core/Naming.cs` | Nomes de arquivo seguros, nomes únicos, limite de caminho, ordem natural |
| `Core/MergePlanner.cs` | Posiciona as folhas lado a lado e monta os comandos do `cadcraft-cli` para cada modo |
| `Core/CadcraftOutput.cs` | Lê o que o `cadcraft-cli` imprime: limites do desenho, resumo e perdas avisadas |
| `Core/Settings.cs` | Configurações em JSON, pacote `.zip` de configurações com manifesto, caminhos |
| `Core/Updates.cs` | Aviso de atualização; só considera tags `dwg-v*` |
| `Revit/SheetSource.cs` | Folhas e vistas exportáveis |
| `Revit/ExportService.cs` | Plano (nomes, conflitos, caminhos longos) e exportação pelo motor do Revit |
| `Bridge/CadcraftBridge.cs` | Roda o `cadcraft-cli` como processo externo, com tempo limite |
| `UI/ExportWindow.*` | A janela de exportação |
| `Addin/*` | Faixa de opções, comandos, fluxos e aviso de atualização |

### Decisões

- **Projeto separado, instalador comum.** O plugin tem a própria DLL, o próprio `.addin` e a própria versão, e entra
  na mesma aba "Gestão BIM". O `Install.ps1` instala todo `.addin` que estiver no pacote; ferramentas compartilhadas
  ficam em `%APPDATA%\GestaoBIM\Tools`, uma cópia só para todos os anos do Revit.
- **Exportar sempre pelo motor do Revit.** O Revit gera um DWG por folha (`Document.Export`). A mesclagem só lê esses
  arquivos; nunca toca no projeto.
- **`cadcraft-cli` original, sem modificação.** É baixado de uma versão fixa, conferido por SHA-256 e embutido com as
  licenças (MIT/Apache-2.0, OFL das fontes). Como processo externo, sem biblioteca vinculada e sem alterar nada do
  código dele. As marcas ArtCraft e CADCraft não são usadas como nome do nosso produto.
- **Como uma mesclagem funciona.** Para cada folha, `cadcraft-cli info` dá os limites do conteúdo. As folhas são
  importadas lado a lado (com um espaço de 25% da maior largura) e, conforme o modo, cria-se um layout com uma
  janela de visualização do tamanho da página enquadrando cada folha (`layout.new`, `pagesetup`, `mview`, `zoom`).
  No layout único, o papel mantém o lado maior do tamanho escolhido e segue a proporção do conjunto.
- **Sem telemetria.** Nada é enviado a lugar nenhum além da consulta de versão ao GitHub (uma vez por dia, em segundo
  plano; `GB_DWG_EXPORT_NO_UPDATE_CHECK=1` desliga).
- **Sem encerrar processos de terceiros.** Não há AutoCAD no fluxo.

### Diferenças em relação a outros exportadores

- **Layout único** aqui é feito pela mesclagem (um layout mostrando todas as folhas), e não por uma folha temporária
  no Revit. O resultado para o cliente é parecido, e o projeto do Revit nunca é alterado.
- Não há agrupamento por conjuntos de folhas nesta versão.

### Testes

- `dotnet test tests/GBDwgExport.Core.Tests`: nomes, ordem natural, planejamento da mesclagem, configurações, pacote de
  configurações, regras de atualização e leitura da saída do motor.
- A camada do Revit (exportação, janela, faixa) só pode ser testada dentro do Revit.

### Variáveis de ambiente

| Variável | Efeito |
|---|---|
| `GB_DWG_EXPORT_HOME` | Muda a pasta de configurações (padrão `%APPDATA%\GestaoBIM\DwgExport`) |
| `GB_CADCRAFT_CLI` | Usa outro `cadcraft-cli.exe` |
| `GB_DWG_EXPORT_NO_UPDATE_CHECK=1` | Desliga a consulta automática de versão |

## Publicar uma versão

Igual ao [GB Material Studio](RELEASE.md), com estas diferenças: o script é `build/package-dwg.ps1`, a versão está em
`src/GBDwgExport/GBDwgExport.csproj`, a tag é `dwg-v<versão>` e a release deve ser criada com `--latest=false`, para
que o aviso do GB Material Studio (que consulta a "última release") continue apontando para ele.

```powershell
./build/package-dwg.ps1
gh release create dwg-v<versão> dist/GBDwgExport-<versão>.zip dist/GBDwgExport-<versão>.zip.sha256 `
  dist/GBDwgExport-Setup-<versão>.exe dist/GBDwgExport-Setup-<versão>.exe.sha256 `
  --latest=false --title "GB DWG Export <versão>" --notes-file notas.md
```
