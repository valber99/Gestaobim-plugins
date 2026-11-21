# Changelog

## 0.3.1

### Novo
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
