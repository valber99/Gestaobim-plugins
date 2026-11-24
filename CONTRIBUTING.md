# Como contribuir com o Gestão BIM Plugins

Obrigado pelo interesse em contribuir com o ecossistema **Gestão BIM** para Revit!
Toda contribuição é bem-vinda — desde relatos de bugs e sugestões de melhoria até correções, novos recursos, testes e documentação.

## Formas de contribuir

- **Reportar bugs**: abra uma [Issue de bug](https://github.com/valber99/Gestaobim-plugins/issues/new?template=bug_report.yml) com a versão do Revit, a versão do plugin e os passos para reproduzir.
- **Sugerir melhorias**: abra uma [Issue de sugestão](https://github.com/valber99/Gestaobim-plugins/issues/new?template=feature_request.yml) ou inicie uma conversa em [Discussions](https://github.com/valber99/Gestaobim-plugins/discussions).
- **Enviar código (Pull Request)**: faça um fork, crie uma branch focada na mudança e abra um Pull Request.

---

## Preparando o ambiente

Você **não precisa ter o Revit instalado** para compilar os plugins ou rodar os testes unitários: a API do Revit vem via NuGet (`Nice3point.Revit.Api`).

### Pré-requisitos
- **Git**
- **.NET 10 SDK** (compila os projetos `net8.0` / `net8.0-windows`)
- **Rust (stable)** — opcional, apenas se você for modificar o motor de busca `tools/gbfam`
- **Windows + Revit 2025/2026/2027** — necessário apenas para testar a interface dentro do Revit real

### Passo a passo

1. Faça um **Fork** deste repositório e clone o seu fork:
   ```powershell
   git clone https://github.com/<seu-usuario>/Gestaobim-plugins.git
   cd Gestaobim-plugins
   ```
2. Crie uma branch descritiva a partir da `main`:
   ```powershell
   git checkout -b fix/descricao-curta
   ```
3. Faça suas alterações e rode os testes:
   ```powershell
   # Testes unitários .NET (todas as suítes)
   dotnet test tests/GBMaterialStudio.Core.Tests
   dotnet test tests/GBDwgExport.Core.Tests
   dotnet test tests/GBFamilias.Core.Tests
   dotnet test tests/GBCotas.Core.Tests
   dotnet test tests/GBRender.Core.Tests
   dotnet test tests/GBAi.Shared.Tests

   # Testes do motor gbfam (se alterou tools/gbfam)
   cargo test --release --manifest-path tools/gbfam/Cargo.toml
   ```
4. Verifique se os add-ins afetados compilam para as **três versões do Revit** (`2025`, `2026` e `2027`):
   ```powershell
   foreach ($year in 2025, 2026, 2027) {
       dotnet build src/GBDwgExport -c Release -p:RevitVersion=$year --nologo
   }
   ```
   *(Substitua `GBDwgExport` pelo plugin alterado: `GBMaterialStudio`, `GBFamilias`, `GBCotas` ou `GBRender`.)*

---

## Diretrizes de código

- **Separação Core × Revit**: mantenha lógica de negócio, parsing, filtros e regras puras nos projetos `*.Core` (sem dependência da API do Revit), com testes unitários xUnit que rodam tanto em Windows quanto em Linux.
- **Compatibilidade multi-versão**: todo add-in precisa compilar com **0 erros e 0 avisos** para Revit **2025, 2026 e 2027**.
- **Interface e idioma**: textos voltados ao usuário final devem estar em **português do Brasil**, claros e diretos.
- **Ícones do Ribbon**: ícones dos botões devem estar em `64×64` pixels com **`192 DPI`** (`32×32 DIPs` no WPF/Revit) para permanecerem nítidos em telas HiDPI sem cortar no Ribbon.
- **Segurança**: nunca inclua chaves de API, tokens, senhas ou caminhos pessoais da sua máquina nos commits.
- **Não altere a versão dos `.csproj` nem o `CHANGELOG.md`** no PR, salvo quando solicitado — o versionamento e a geração do instalador único assinado (`GestaoBIM-Setup-<versão>.exe`) são feitos na publicação da release.

---

## Pull Requests e CI

Ao abrir um Pull Request para a `main`, o GitHub Actions ([`.github/workflows/tests.yml`](.github/workflows/tests.yml)) executa automaticamente:
- Todas as suítes `.NET` em Linux e Windows;
- Compilação dos 5 add-ins para Revit 2025, 2026 e 2027;
- Testes em Rust do `gbfam` e testes em Python da edição legada pyRevit.

Aguarde o CI ficar verde e responda aos comentários da revisão. Mudanças menores e bem focadas são revisadas e integradas muito mais rápido!

## Licença

Ao contribuir com este repositório, você concorda que suas contribuições serão licenciadas sob a mesma licença do projeto: **[GNU Affero General Public License v3.0 (AGPL-3.0)](LICENSE)**.
