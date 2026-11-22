# Guia do usuário: GB Material Studio

Estúdio de materiais para o Revit 2025, 2026 e 2027. Não precisa de pyRevit nem de Python.

## Instalar, atualizar e remover

**Instalar ou atualizar**

1. Baixe o instalador único `GestaoBIM-Setup-<versão>.exe` na página de
   [Releases](https://github.com/valber99/Gestaobim-plugins/releases). Ele traz os três plugins Gestão BIM.
2. **Feche o Revit** e dê dois cliques no `.exe`. Ele instala em todos os Revit 2025, 2026 e 2027 que encontrar,
   confere a integridade do pacote, não pede administrador e recusa instalar se o Revit estiver aberto.
3. O Windows pode avisar que o editor é desconhecido, porque o instalador não é assinado. Clique em
   "Mais informações" e depois em "Executar assim mesmo".
4. Abra o Revit. Na primeira vez ele pergunta se confia no add-in: escolha **"Sempre carregar"**.

Quem prefere pode baixar o `.zip`, descompactar e executar `Instalar.bat`. O resultado é o mesmo.

**Remover:** `Desinstalar.bat` (dentro do `.zip`). Os favoritos e a biblioteca não são apagados.

**Edição antiga em pyRevit:** o instalador move `GBMaterialStudio.extension` e `ValberMaterialStudio.extension`
para uma pasta de backup em `%APPDATA%\pyRevit`, para a aba "Gestão BIM" não aparecer duplicada. Seus
favoritos e configurações são copiados para a pasta nova na primeira abertura; a pasta antiga não é alterada.

## Onde fica

Aba **Gestão BIM**, painel **Materiais**: Editar material, Editor fixo, Favoritos, Organizar materiais e
Aplicar em tipo. O botão **Sobre** é um só para todos os plugins Gestão BIM e fica no painel **Geral**.

## Editar material

1. Clique em **Editar material** e aponte uma face no modelo (o cursor vira um conta-gotas).
2. O editor abre com o material daquela face. As abas cobrem identidade e informações gerais, cor, padrões de
   superfície e de corte (primeiro plano e fundo), aparência de renderização, texturas e favoritos.
3. **Aplicar** grava as alterações. Tudo é uma única entrada de desfazer (Ctrl+Z) e é tudo ou nada: se algo
   falhar, nada é alterado.
4. **Novo material** cria um material a partir do que está na tela. **Pintar faces** e **Aplicar nas faces**
   aplicam o material em outras faces do modelo; Esc encerra a pintura.
5. Para ajustar uma textura (escala, posição, repetição, rotação, brilho, inverter), use **Alterar textura**.

**Editor fixo** abre o mesmo editor numa janela que não bloqueia o Revit: você continua navegando e
selecionando enquanto edita.

## Favoritos

A janela lista os materiais favoritos, com busca por nome ou projeto de origem e uma prévia com os detalhes.

- **Favoritar** (no editor) copia o material, completo e com a aparência, para a biblioteca.
- **Abrir editor** edita o favorito. **Aplicar nas faces** pinta faces do modelo com ele.
- **Atualizar** recarrega a lista sem perder dados.
- O favorito funciona em qualquer projeto, sem precisar abrir o projeto de onde veio.

### Biblioteca portátil

Cada favorito fica num arquivo `.rvt` de biblioteca, um por versão do Revit (`GBMaterialLibrary-<versão>.rvt`),
porque um `.rvt` salvo numa versão não abre em versão anterior.

- Pasta padrão: `%APPDATA%\GestaoBIM\MaterialStudio`.
- Para usar outra pasta (por exemplo, uma pasta de rede), crie
  `%APPDATA%\GestaoBIM\MaterialStudio\gb-settings.json` com `{"library_dir": "D:\\Biblioteca"}`.
- Texturas continuam sendo arquivos no disco: o caminho precisa existir na máquina de destino. Se alguma não for
  encontrada, o material é transferido e você recebe um aviso.
- Se o projeto já tem um material com o mesmo nome e o mesmo conteúdo, ele é reutilizado. Com o mesmo nome e
  conteúdo diferente, a transferência é recusada.

## Organizar materiais

Mostra cada material com a situação (usado, não usado, duplicado) e o número de usos.

- **Filtros:** Todos, Não usados, Duplicados, e busca por nome, classe ou "duplicado de".
- **Selecionar não usados** e **Excluir não usados selecionados**. Um material só é tratado como não usado
  quando a varredura própria e o `GetUnusedElements` do Revit (o critério do Purge Unused) concordam. Cada
  exclusão é desfeita se removeria outros elementos.
- **Selecionar duplicados** marca os materiais com o mesmo conteúdo, para você decidir o que manter.
- **Renomear em lote:** localizar e substituir (texto simples ou expressão regular, com `\1` para grupos),
  prefixo, sufixo e caixa (MAIÚSCULAS, minúsculas, Primeira maiúscula, Cada Palavra). **Pré-visualizar**
  mostra o novo nome e a situação antes de **Aplicar renomeação**. Aplica aos selecionados; sem seleção,
  a todos os filtrados.
- Excluir ou renomear é uma entrada só de desfazer.

## Aplicar em tipo

Troca o material das camadas de tipos de parede, piso, cobertura e forro.

1. Selecione elementos no modelo (o botão usa os tipos deles) ou escolha os tipos numa lista.
2. Escolha o material de destino: um material do projeto ou um favorito.
3. Confirme. A troca é uma entrada só de desfazer.

## Sobre e atualizações

**Sobre** (painel **Geral**) é um botão **único**, compartilhado por todos os plugins Gestão BIM instalados (Material
Studio, DWG Export e Famílias). Ele mostra, numa janela só, a versão instalada de cada um, o diagnóstico de cada um e
consulta o GitHub na hora. Se algum tiver versão nova, oferece **Baixar** (abre a página da release). Os add-ins não se
atualizam sozinhos: baixe e rode o instalador com o Revit fechado.

Ao abrir o Revit, o add-in consulta o GitHub em segundo plano (no máximo uma vez por dia). Se houver versão
nova de qualquer plugin, o botão **Sobre** ganha uma **bolinha vermelha**. Sem internet, nada acontece.

Para desligar a consulta automática, defina a variável de ambiente `GB_MATERIAL_STUDIO_NO_UPDATE_CHECK=1`.

## Onde ficam os dados

Tudo em `%APPDATA%\GestaoBIM\MaterialStudio`:

| Arquivo | Conteúdo |
|---|---|
| `favorites-v4.json` | Favoritos (com backups automáticos) |
| `gb-settings.json` | Configurações (pasta da biblioteca) |
| `GBMaterialLibrary-<versão>.rvt` | Biblioteca portátil de materiais |
| `update-check.json` | Quando foi a última consulta de versão |

Variável opcional `GB_MATERIAL_STUDIO_HOME` muda essa pasta.

## Problemas comuns

| Sintoma | O que fazer |
|---|---|
| A aba "Gestão BIM" não aparece | Confirme que o instalador rodou com o Revit fechado e que você escolheu "Sempre carregar". |
| O Revit mostra "Complemento não certificado" | É esperado: o add-in não é assinado. Escolha "Sempre carregar". |
| A aba aparece duplicada | A edição antiga em pyRevit ainda está instalada. Rode o instalador de novo (ele a move para backup). |
| "O Revit está aberto" no instalador | Feche o Revit e execute o instalador de novo. |
| Textura não aparece no material transferido | O arquivo da imagem não existe nesta máquina. Copie a imagem para o mesmo caminho. |
| Erro ao abrir a biblioteca | Veja se a pasta em `gb-settings.json` existe e se há permissão de escrita. Feche outro Revit que esteja usando a biblioteca. |

Encontrou um defeito? Abra uma issue em https://github.com/valber99/Gestaobim-plugins/issues.
