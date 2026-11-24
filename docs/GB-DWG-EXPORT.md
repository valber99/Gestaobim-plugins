# GB DWG Export

Plugin do Revit (2025, 2026 e 2027) que exporta folhas e vistas para DWG/DXF e junta tudo num só desenho, **sem
precisar do AutoCAD**: cada folha vira um layout igual à folha do Revit, e as vistas ficam lado a lado no model space.
Faz parte do Gestão BIM e usa o mesmo instalador e a mesma aba do GB Material Studio.

## Guia do usuário

### Instalar

1. Baixe o instalador único `GestaoBIM-Setup-<versão>.exe` na página de
   [Releases](https://github.com/valber99/Gestaobim-plugins/releases) (tag `v<versão>`; ele traz os cinco plugins Gestão BIM).
2. Feche o Revit e dê dois cliques no `.exe`. Ele recusa instalar com o Revit aberto.
3. O Windows pode avisar que o editor é desconhecido (o instalador não é assinado): "Mais informações" e
   "Executar assim mesmo".
4. Abra o Revit. Na primeira vez ele pergunta se confia no add-in: escolha **"Sempre carregar"**.

O pacote traz o `cadcraft-cli` (o motor de mesclagem, 67 MB) e o instala em `%APPDATA%\GestaoBIM\Tools\cadcraft`. Não é
preciso instalar o CADCraft nem o AutoCAD. Também há um `.zip` com `Instalar.bat` e `Desinstalar.bat` (o instalador
também copia `Desinstalar.bat` para `%APPDATA%\GestaoBIM\Desinstalar.bat`).

### Onde fica

Aba **Gestão BIM**, painel **Exportar DWG**:

| Botão | O que faz |
|---|---|
| **Exportar DWG** | Abre a janela unificada de exportação de **folhas** ou **vistas** para DWG/DXF: um só DWG com um layout por folha (igual à folha do Revit), tudo no model space, layout único ou arquivos separados — além de **Exportar / Importar configurações** (`.zip`). |
| **Sobre** (painel **Geral**) | Botão único de todos os plugins Gestão BIM: mostra a versão de cada um e consulta o GitHub; uma bolinha vermelha avisa que há versão nova. |

### A janela de exportação

- **Lista:** folhas (ou vistas) em ordem natural (A2 vem antes de A10), com busca por número, nome ou tipo.
  **Marcar todas** marca as que estão visíveis na busca.
- **Resultado:** por padrão, **um só desenho com um layout por folha**. Cada layout é o espaço de papel que o Revit
  exporta para a folha (carimbo, tabelas, notas, legendas e as viewports), e o model space junta as vistas de todas
  as folhas, lado a lado. As outras opções na janela são tudo no model space (sem layouts), um só layout com todas as folhas
  lado a lado, e "um arquivo por folha ou vista (sem juntar)", que é o que o Revit exporta sozinho. Para vistas existem
  "tudo no model space" (as vistas marcadas lado a lado num só DWG) e um arquivo por vista.
- **Papel dos layouts:** não é uma escolha. Cada layout usa **o papel da folha no Revit**, lido do **carimbo** da
  folha (por exemplo, A1, A0 ou A3), em paisagem ou retrato como a folha está. Medidas a até 3 mm de um papel A0 a A4
  (o Revit costuma informar 840 x 594 para um A1) viram o papel exato; um tamanho fora do padrão é mantido como
  está. Uma folha sem carimbo usa o contorno da folha. A página é plotada em escala 1:1 com o papel (num desenho em
  metros, 1 unidade = 1000 mm).
- **Vistas fora do carimbo:** se a folha no Revit tem vistas "estacionadas" fora do carimbo, elas também vão para o
  layout, na mesma posição, fora da página (como no Revit). Para que não apareçam, tire-as da folha antes de exportar.
- **Arquivo:** DWG ou DXF; nome "Número - Nome da folha" ou só o número (nos arquivos separados); e a
  **configuração de exportação do Revit** (as de *Exportar > Opções > Configuração de DWG/DXF* do projeto). Sem
  escolher, o plugin usa padrões: vistas mescladas, planos de referência e caixas de corte ocultos, cores indexadas,
  sólidos como malha e a unidade do projeto. O desenho único leva o nome do projeto. Ao juntar, as vistas são sempre
  exportadas mescladas na folha, mesmo que a configuração escolhida as exporte como referências externas (xref):
  esses arquivos ficariam numa pasta temporária e o desenho perderia as vistas. As imagens que o Revit grava ao lado
  do DWG (logos, renders) são copiadas para a pasta de destino junto com o desenho.
- **Pasta de destino** e **Abrir a pasta ao terminar**. As escolhas ficam salvas em `%APPDATA%\GestaoBIM\DwgExport`.

### Nomes de arquivos e erros

- Acentos são removidos e `\ / : * ? " < > |` viram `_`. Nome vazio vira "Export". Nomes repetidos ganham `_2`, `_3`.
- O caminho completo é conferido **antes** de exportar. Se passar de 259 caracteres, o plugin oferece a Área de
  Trabalho, numa pasta com o nome do projeto; se ainda ficar longo, lista os arquivos e pede para encurtar.
- Arquivo que já existe: pergunta "Deseja sobrescrever?".
- Uma folha que falha não derruba as outras: o resumo no fim lista as falhas.
- O Revit deixa um arquivo `.pcp` (configuração de plotagem) ao lado de cada exportação; o plugin o remove, a menos que
  você já tivesse um com o mesmo nome. Um arquivo existente que você aceitou sobrescrever é apagado antes de exportar.
- Se a mesclagem falhar, os arquivos que o Revit já exportou são guardados na pasta `<projeto> - individuais`, para
  você nunca perder o trabalho.
- Uma janela de andamento mostra qual folha está sendo exportada. Como a API do Revit só roda na thread do próprio
  Revit, a exportação não acontece em segundo plano: durante a mesclagem, que roda num programa à parte, a janela
  não atualiza e o Revit pode parecer parado até ela terminar.
- Nomes de layout reservados ("Model", "Layout1", "Layout2") ganham o sufixo `_2`.
- O resumo também mostra **o que a mesclagem não conseguiu manter**, quando o motor avisa (ver abaixo).

### Sobre a mesclagem e o que ela pode perder

A mesclagem lê os DWG exportados pelo Revit e grava um novo DWG com o `cadcraft-cli`. O CADCraft é um projeto jovem
(versão 0.20, em fase alfa), e **ele mesmo documenta que salvar DWG descarta o que ele não modela** e que o DWG
gerado nunca foi conferido no AutoCAD. Nos testes deste plugin, DWG e DXF reais do Revit mantiveram o mesmo número de
entidades, camadas, blocos e layouts; a única perda avisada foi de cotas em arco (`ARC_DIMENSION`) num DWG de 20 MB.
Por isso:

- o resumo mostra as perdas que o motor avisa;
- se precisar do conteúdo completo, "Um arquivo por folha ou vista (sem juntar)" entrega o que o Revit exporta, sem
  passar pelo motor;
- **abra o resultado no AutoCAD ou no DWG TrueView e confira antes de entregar ao cliente.**

Fontes SHX não são lidas pelo motor, e entidades que ele não conhece podem ser descartadas sem aviso.

## Arquitetura

```
src/GBDwgExport.Core   regras puras, sem Revit (testadas em Windows e Linux)
src/GBDwgExport        o add-in: faixa, comandos, janela WPF, exportação e ponte com o motor
tests/GBDwgExport.Core.Tests   testes xUnit do Core (e integração com o cadcraft-cli, quando instalado)
build/package-dwg.ps1  compila 2025/2026/2027, embute o cadcraft-cli e gera .zip e .exe
build/revit-selftest-dwg.ps1   autoteste dentro do Revit (ver "Testes")
```

| Arquivo | Papel |
|---|---|
| `Core/Naming.cs` | Nomes de arquivo seguros, nomes únicos, limite de caminho, ordem natural |
| `Core/MergePlanner.cs` | Posiciona as vistas lado a lado no model space, ajusta o papel ao padrão A0 a A4 e monta os comandos do `cadcraft-cli` para cada modo: copiar o espaço de papel de cada folha, a página, a limpeza dos layouts vazios. Divide mesclagens longas em várias execuções (`PlanRuns`) |
| `Core/CadcraftOutput.cs` | Lê o que o `cadcraft-cli` imprime: limites do desenho, resumo, perdas avisadas, o layout da folha (`INSUNITS` e `layout.list`) e as viewports |
| `Core/Settings.cs` | Configurações em JSON, pacote `.zip` de configurações com manifesto, caminhos |
| `Core/Updates.cs` | Aviso de atualização; considera as tags `v*` do instalador único |
| `Revit/SheetSource.cs` | Folhas e vistas exportáveis; papel e canto de cada folha, lidos do carimbo |
| `Revit/ExportService.cs` | Plano (nomes, conflitos, caminhos longos), exportação pelo motor do Revit (vistas sempre mescladas ao juntar) e cópia das imagens |
| `Revit/SelfTest.cs` | Autoteste dentro do Revit; só age com `GB_DWG_EXPORT_SELFTEST` definida |
| `Bridge/CadcraftBridge.cs` | Roda o `cadcraft-cli` como processo externo, com tempo limite; lê o espaço de papel de cada folha e executa a mesclagem |
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
- **Como uma mesclagem funciona.** Para cada arquivo, `cadcraft-cli info` dá os limites do model space, e o model
  space de cada um é importado lado a lado (com um espaço de 25% da maior largura). Para cada folha, o plugin lê o
  layout que o Revit escreveu (o que tem viewports), as unidades (`INSUNITS`) e as viewports. Na mesclagem, ele abre o
  DWG da folha, apaga a viewport geral do layout (a de id 1, que não é uma vista), aponta cada viewport para onde a
  vista foi parar no model space (`viewport.set center`), copia o espaço de papel com o canto inferior esquerdo da
  folha como ponto base (`copybase`), fecha o arquivo sem salvar e cola num layout novo (`pasteclip`). O canto é o
  mínimo da caixa dos carimbos na folha (`get_BoundingBox(sheet)`), porque as coordenadas de folha do Revit são as do
  espaço de papel do DWG. `ViewSheet.Outline` só entra numa folha sem carimbo: ele também cobre vistas deixadas fora
  do carimbo. A página tem o papel da folha, com escala igual a milímetros por unidade. Os Layout1 e Layout2 vazios
  do desenho novo são apagados. No layout único, o espaço de papel de cada folha é colado ao lado do anterior, com
  50 mm de papel entre eles.
  Se o espaço de papel não puder ser lido, a folha volta ao método antigo (uma janela de visualização enquadrando o
  model space). Como o Windows limita a linha de comando a 32 767 caracteres, uma mesclagem longa é dividida em
  várias execuções, cada uma reabrindo o desenho que a anterior salvou.
- **Sem telemetria.** Nada é enviado a lugar nenhum além da consulta de versão ao GitHub (uma vez por dia, em segundo
  plano; `GB_NO_UPDATE_CHECK=1` ou `GB_DWG_EXPORT_NO_UPDATE_CHECK=1` desliga).
- **Sem encerrar processos de terceiros.** Não há AutoCAD no fluxo.

### Diferenças em relação a outros exportadores

- **Layout único** aqui é feito pela mesclagem (as folhas inteiras lado a lado num layout), e não por uma folha
  temporária no Revit. O resultado para o cliente é parecido, e o projeto do Revit nunca é alterado.
- Não há agrupamento por conjuntos de folhas nesta versão.

### Testes

- `dotnet test tests/GBDwgExport.Core.Tests`: nomes, ordem natural, planejamento da mesclagem (cópia do espaço de
  papel, layout único, divisão em várias execuções), configurações, pacote de configurações, regras de atualização e
  leitura da saída do motor. Com o `cadcraft-cli` instalado, os testes de integração rodam o motor de verdade, inclusive
  numa folha com espaço de papel montada como a do Revit (carimbo com o canto fora da origem e viewport a 1:100).
- A camada do Revit é testada dentro do Revit, numa sessão própria:

  ```powershell
  ./build/revit-selftest-dwg.ps1 -Year 2025 -Models C:\caminho\projeto1.rvt, C:\caminho\projeto2.rvt -WorkDir F:\teste
  ```

  O script compila e instala o plugin só no Revit daquele ano (que precisa estar fechado; os outros anos podem estar
  abertos), copia os projetos para `-WorkDir` e abre o Revit com `GB_DWG_EXPORT_SELFTEST`. O `Revit/SelfTest.cs` abre
  cada cópia desanexada, escolhe as duas folhas com mais vistas e exporta em cada modo de mesclagem, com e sem a
  configuração de exportação do projeto, e as vistas no model space. Ele confere se há um layout por folha, uma
  viewport por viewport do Revit (legendas incluídas), o carimbo no papel, o model space com as vistas e a ausência de
  Layout1/Layout2. Nunca salva os projetos. O relatório e um PNG de cada layout ficam em `<WorkDir>\saida`. Os
  temporários do Revit também vão para `-WorkDir` (projetos grandes pedem alguns GB livres; o padrão é `%TEMP%`).
  Na 0.4.1 passou no Revit 2025 com dois projetos reais (65 verificações).
- A janela e a faixa são testadas à mão.

### Variáveis de ambiente

| Variável | Efeito |
|---|---|
| `GB_DWG_EXPORT_HOME` | Muda a pasta de configurações (padrão `%APPDATA%\GestaoBIM\DwgExport`) |
| `GB_CADCRAFT_CLI` | Usa outro `cadcraft-cli.exe` |
| `GB_NO_UPDATE_CHECK=1` / `GB_DWG_EXPORT_NO_UPDATE_CHECK=1` | Desliga a consulta automática de versão |
| `GB_DWG_EXPORT_SELFTEST` | Pasta do relatório do autoteste; com ela, o plugin roda o autoteste ao abrir o Revit (só para testes) |
| `GB_DWG_EXPORT_SELFTEST_MODELS` | Projetos `.rvt` do autoteste, separados por `;` |

## Publicar uma versão

Desde a 0.4.0 o DWG Export sai no instalador único Gestão BIM, com a mesma versão dos outros plugins e a tag
`v<versão>`: veja [RELEASE.md](RELEASE.md). `build/package-dwg.ps1` continua gerando um pacote só dele para
desenvolvimento (embute o cadcraft-cli conferido por checksum). As releases antigas tinham a tag `dwg-v<versão>`.
