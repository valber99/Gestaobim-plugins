# Gestaobim-plugins

Plugins do ecossistema **Gestão BIM** para o Revit (**2025, 2026 e 2027**).

> **Cadastro obrigatório para acessar a biblioteca GB Famílias (41.762 famílias):**
> acesse **[https://cadastro.gestaobim.com](https://cadastro.gestaobim.com)** para criar sua conta e solicitar acesso.
> Após a liberação pelo administrador, basta clicar em **Entrar** no plugin dentro do Revit com o e-mail e a senha cadastrados.

O repositório reúne **cinco plugins** em um **instalador gráfico único** (`GestaoBIM-Setup-<versão>.exe`, mesma versão
para os cinco, tag `v<versão>`). Todos são **add-ins nativos (.NET)**: não precisam de pyRevit nem de Python, e dividem
a aba **Gestão BIM** no Revit.

| Plugin | Painel no Ribbon | Para que serve |
|---|---|---|
| **GB Material Studio** | Materiais | Estúdio de materiais: editor instantâneo, editor fixo ao lado do Revit, favoritos portáteis, organização/limpeza segura e troca de material por camada de tipo |
| **GB DWG Export** | Exportar DWG | Um único botão que abre a interface unificada para exportar folhas ou vistas em DWG/DXF (múltiplos layouts, model space, layout único ou arquivos separados), sem AutoCAD ([detalhes](docs/GB-DWG-EXPORT.md)) |
| **GB Famílias** | Famílias | Biblioteca com **41.762 famílias** com busca por texto e por imagem (IA local em RAM), filtros, categorias, favoritos e coleções; baixa e insere no projeto ([detalhes](docs/GB-FAMILIAS.md)) |
| **GB Cotas** | IA | Cotagem automática de plantas com cotas nativas do Revit sem sobreposição — funciona 100% por regras (NBR 6492) ou com planejamento opcional por IA ([detalhes](docs/GB-COTAS-E-RENDER.md)) |
| **GB Render** | IA | Renderização arquitetônica com IA a partir da vista 3D do Revit (com camadas estruturais/clay, pré-visualização ao vivo e edição localizada por elemento) ([detalhes](docs/GB-COTAS-E-RENDER.md)) |

O instalador leva os cinco plugins, o catálogo local de famílias (banco com previews e embeddings), o motor de busca por
IA (`gbfam.exe`) e o gateway local de IA (`CLIProxyAPI`). Apenas os arquivos `.rfa` das famílias ficam no servidor e são
baixados sob demanda, com login, quando você insere a família.

## Instalação

1. Baixe na página de [Releases](https://github.com/valber99/Gestaobim-plugins/releases/latest) o instalador
   **`GestaoBIM-Setup-<versão>.exe`** (ou o `.zip`, se preferir instalar pelo `Instalar.bat`).
2. Feche o Revit e dê dois cliques no `.exe`. Ele abre uma janela gráfica nativa do Windows para escolher as versões do
   Revit (2025, 2026 e/ou 2027), confere a integridade SHA-256 do pacote, recusa instalar com o Revit aberto e **não
   pede administrador**. Também aceita `/quiet` para instalação silenciosa.
   > O Windows SmartScreen pode avisar que o editor é desconhecido (o instalador ainda não possui assinatura digital):
   > clique em **"Mais informações"** → **"Executar assim mesmo"**.
3. Abra o Revit. Na primeira abertura, clique em **"Sempre carregar"** nos avisos de add-in.
4. **Para usar a biblioteca do GB Famílias:** faça seu cadastro em
   **[https://cadastro.gestaobim.com](https://cadastro.gestaobim.com)** e aguarde a liberação do acesso.
   Enquanto isso, a busca local já funciona normalmente.

Quando sair uma versão nova, o botão único **Sobre** (no painel **Geral**) mostra uma bolinha vermelha e leva direto
para o download do instalador único. Para desativar a checagem automática, defina a variável de ambiente
`GB_MATERIAL_STUDIO_NO_UPDATE_CHECK=1`.

Para remover os plugins, execute `Desinstalar.bat`. Seus favoritos, bibliotecas e configurações não são apagados.

**Requisitos:** Windows e **Revit 2025, 2026 ou 2027**.

---

## O que o GB Material Studio faz

Tudo fica na aba **Gestão BIM**, painel **Materiais**:

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

---

## Desenvolvimento

Um código-fonte, três compilações (Revit 2025, 2026 e 2027), com a API do Revit vinda do NuGet
(`Nice3point.Revit.Api`) — **não é preciso ter o Revit instalado para compilar ou rodar os testes unitários**.

### Pré-requisitos
- **.NET 10 SDK** (os projetos miram `net8.0` / `net8.0-windows`, compilados pelo SDK 10)
- **Rust (stable)** — apenas se for alterar o motor de busca `tools/gbfam`

### Comandos principais

```powershell
# Rodar todas as 6 suítes de testes .NET (485 testes)
dotnet test tests/GBMaterialStudio.Core.Tests
dotnet test tests/GBDwgExport.Core.Tests
dotnet test tests/GBFamilias.Core.Tests
dotnet test tests/GBCotas.Core.Tests
dotnet test tests/GBRender.Core.Tests
dotnet test tests/GBAi.Shared.Tests

# Compilar um plugin específico para um ano do Revit (2025, 2026 ou 2027)
dotnet build src/GBMaterialStudio -c Release -p:RevitVersion=2027
dotnet build src/GBDwgExport -c Release -p:RevitVersion=2027
dotnet build src/GBFamilias -c Release -p:RevitVersion=2027
dotnet build src/GBCotas -c Release -p:RevitVersion=2027
dotnet build src/GBRender -c Release -p:RevitVersion=2027

# Testes do motor de busca em Rust (GB Famílias)
cargo test --release --manifest-path tools/gbfam/Cargo.toml

# Gerar o instalador único completo (requer catálogo/modelos locais; ver docs/RELEASE.md)
./build/package-gestaobim.ps1
```

### Estrutura do repositório

- `src/GBMaterialStudio*` e `tests/GBMaterialStudio.Core.Tests` — GB Material Studio (regras puras + add-in WPF).
- `src/GBDwgExport*` e `tests/GBDwgExport.Core.Tests` — GB DWG Export (integração com `cadcraft-cli`).
- `src/GBFamilias*`, `tests/GBFamilias.Core.Tests` e `tools/gbfam` — GB Famílias (interface WPF + motor Rust/ONNX).
- `src/GBAi.Shared`, `src/GBAi.Revit`, `tests/GBAi.Shared.Tests` — camada compartilhada de IA, DPAPI e gateway local.
- `src/GBCotas*` e `tests/GBCotas.Core.Tests` — GB Cotas.
- `src/GBRender*` e `tests/GBRender.Core.Tests` — GB Render.
- `src/Shared` — componentes compartilhados entre os 5 plugins (como a janela unificada `AboutHub`).
- `build/` e `installer/` — scripts de empacotamento, instalador gráfico nativo (`SetupStub.cs`) e manifestos `.addin`.
- `GBMaterialStudio.extension/` — **edição legada em pyRevit** (mantida apenas como referência).

### Documentação

- [`CONTRIBUTING.md`](CONTRIBUTING.md): como contribuir com código, reportar bugs ou sugerir melhorias.
- [`docs/GUIA-DO-USUARIO.md`](docs/GUIA-DO-USUARIO.md): guia de uso, botões, onde ficam os dados e solução de problemas.
- [`docs/ARQUITETURA.md`](docs/ARQUITETURA.md): organização interna e decisões técnicas.
- [`docs/GB-DWG-EXPORT.md`](docs/GB-DWG-EXPORT.md): guia e arquitetura do GB DWG Export.
- [`docs/GB-FAMILIAS.md`](docs/GB-FAMILIAS.md): guia e arquitetura do GB Famílias.
- [`docs/GB-COTAS-E-RENDER.md`](docs/GB-COTAS-E-RENDER.md): guia de configuração de IA, GB Cotas e GB Render.
- [`docs/RELEASE.md`](docs/RELEASE.md): como gerar e publicar novas versões.
- [`CHANGELOG.md`](CHANGELOG.md): histórico completo de versões.

### Edição legada em pyRevit

Antes do add-in nativo, o Material Studio existia como extensão do pyRevit (`GBMaterialStudio.extension`, Python 3).
Ela continua no repositório com testes (`python -m pytest -q`), mas **não é mais a edição recomendada**. Se as duas
estiverem instaladas, a aba "Gestão BIM" aparece duplicada; o instalador move a edição pyRevit para fora do caminho
automaticamente.

## Contribuindo

Contribuições são muito bem-vindas! Leia o guia em [`CONTRIBUTING.md`](CONTRIBUTING.md), abra uma
[Issue](https://github.com/valber99/Gestaobim-plugins/issues) para bugs/melhorias ou participe das
[Discussions](https://github.com/valber99/Gestaobim-plugins/discussions) para dúvidas e ideias.

## Licença

[GNU Affero General Public License v3.0](LICENSE) (AGPL-3.0).
