# Como publicar uma versão

Os cinco plugins (GB Material Studio, GB DWG Export, GB Famílias, GB Cotas e GB Render) saem juntos, num
**instalador único** e numa **release só** no GitHub, com **uma versão para os cinco** e tag `v<versão>`.

## 1. Preparar

1. Altere `<Version>` nos cinco `.csproj` (`src/GBMaterialStudio`, `src/GBDwgExport`, `src/GBFamilias`, `src/GBCotas`,
   `src/GBRender`) para o mesmo número (por exemplo, `0.5.0`). O script de pacote recusa versões diferentes. Esse número é o que o botão **Sobre**
   compara com a última release `v*` do GitHub.
2. Atualize o `CHANGELOG.md` com uma seção para a versão.
3. Rode os testes:
   ```powershell
   dotnet test tests/GBMaterialStudio.Core.Tests
   dotnet test tests/GBDwgExport.Core.Tests
   dotnet test tests/GBFamilias.Core.Tests
   dotnet test tests/GBAi.Shared.Tests      # inclui o gateway real se build/cache/cliproxy-x/cli-proxy-api.exe existir
   dotnet test tests/GBCotas.Core.Tests
   dotnet test tests/GBRender.Core.Tests
   cargo test --release --manifest-path tools/gbfam/Cargo.toml
   ```

## 2. Empacotar

O pacote é gerado na máquina de quem publica porque leva o `gbfam.exe` e os modelos de IA, que não estão no
repositório, e o **catálogo** de famílias (gerado fora do repositório com `gbfam export-catalog`):

```powershell
cargo build --release --manifest-path tools/gbfam/Cargo.toml     # o gbfam.exe
./build/package-gestaobim.ps1
```

O script compila os cinco plugins para o Revit 2025, 2026 e 2027, junta tudo e gera em `dist/`. O
`package-ia.ps1` (chamado por ele) baixa o CLIProxyAPI da versão fixa em `ProxyPin` (`src/GBAi.Shared/GatewayHost.cs`),
confere o SHA-256 do zip e do executável e recusa se o pino do script e o do código divergirem. Para trocar a versão do
gateway, mude os três valores nos dois lugares e rode `tests/GBAi.Shared.Tests` com o binário novo.

| Arquivo | Para quê |
|---|---|
| `GestaoBIM-Setup-<versão>.exe` e `.sha256` | O instalador (~0,8 GB): os cinco plugins, o catálogo, a busca por IA (gbfam + modelos), o cadcraft-cli e o CLIProxyAPI. Só os `.rfa` ficam de fora. |
| `GestaoBIM-<versão>.zip` e `.sha256` | O mesmo conteúdo, com `Instalar.bat` e `Desinstalar.bat` |

O `.exe` é o instalador próprio `build/SetupStub.cs` (o IExpress travava com esse tamanho), compilado pelo `csc` do .NET
Framework que vem no Windows, com o `.zip` colado no fim. Ele confere o SHA-256 do pacote, recusa instalar com o Revit
aberto, não pede administrador e roda o `Install.ps1` (`/quiet` não pausa no fim). O log fica em
`%TEMP%\gestaobim-setup.log`. O catálogo só é trocado se o do pacote for mais novo que o instalado.

Os scripts de cada plugin (`package.ps1`, `package-dwg.ps1`, `package-familias.ps1`) continuam gerando pacotes
separados para desenvolvimento, mas **não são publicados**. `package-catalog.ps1` gera um pacote só com o catálogo.

## 3. Testar no Revit

O código que usa a API do Revit só pode ser testado dentro do Revit. Com o Revit fechado:

```powershell
./dist/GestaoBIM-Setup-<versão>.exe /quiet
./build/revit-selftest.ps1 -Year 2025 -SkipInstall      # e 2026, 2027
```

O teste abre o Revit com dados isolados e um login de teste e roda o GB Famílias por dentro (login, catálogo, busca,
baixar, carregar e posicionar, família que o usuário já tinha, sem internet, sem login) e o botão **Sobre** dos três.
O DWG Export tem o próprio autoteste, que só exige o Revit daquele ano fechado (os outros anos podem estar abertos):

```powershell
./build/revit-selftest-dwg.ps1 -Year 2025 -Models C:\caminho\projeto1.rvt, C:\caminho\projeto2.rvt -WorkDir F:\teste
```

O GB Cotas e o GB Render têm um autoteste parecido (também só exige o Revit daquele ano fechado):

```powershell
./build/revit-selftest-ia.ps1 -Plugin Cotas -Year 2025 -Models C:\caminho\projeto1.rvt, C:\caminho\projeto2.rvt
./build/revit-selftest-ia.ps1 -Plugin Render -Year 2025 -Models C:\caminho\projeto1.rvt, C:\caminho\projeto2.rvt
```

O de Cotas cota as duas plantas com mais paredes de cada projeto pelas regras (sem IA) e confere 0 colisões, 100% das
cadeias como cota nativa e os valores medidos pelo Revit iguais aos do plano. O de Render extrai a vista 3D, monta as
quatro camadas e o manifesto e compara o enquadramento com a imagem que o próprio Revit exporta da vista. Nenhum dos
dois chama a IA. Detalhes em [PLANO-GB-IA.md](PLANO-GB-IA.md).

O do DWG Export exporta folhas em cada modo de mesclagem (inclusive com a configuração de exportação do projeto) e vistas no model
space, em cópias dos projetos que nunca são salvas, confere layouts, viewports e carimbo, e grava um PNG de cada layout
em `<WorkDir>\saida` para conferir a olho. Use projetos com folhas de verdade (carimbo, várias vistas, legenda). As
cópias e os temporários do Revit ficam em `-WorkDir` (padrão `%TEMP%\gbdwg-selftest-<ano>`): com projetos grandes,
escolha um disco com alguns GB livres, senão o Revit falha ao abrir ("Insufficient disk space"). Ele instala o DWG
Export compilado do código atual no Revit daquele ano. Detalhes em [GB-DWG-EXPORT.md](GB-DWG-EXPORT.md#testes).

Os comandos do Material Studio e as janelas (cliques) continuam sendo testados à mão: abra o Revit,
confira a aba "Gestão BIM", **Configurações → Entrar** e inserir uma família que ainda não foi baixada.

Antes de publicar, confira também o status do servidor (saúde do serviço de arquivos). A versão do plugin precisa ser
igual ou maior que o `min_plugin` do catálogo publicado.

## 4. Publicar

```powershell
git push                      # como valber99, ver a nota abaixo
gh release create v<versão> `
  dist/GestaoBIM-Setup-<versão>.exe dist/GestaoBIM-Setup-<versão>.exe.sha256 `
  dist/GestaoBIM-<versão>.zip dist/GestaoBIM-<versão>.zip.sha256 `
  --title "Gestão BIM <versão>" --notes-file notas.md --latest
```

Nas notas, diga o que foi verificado em execução e o que não foi, e lembre do aviso "Complemento não certificado" e
do aviso do Windows sobre o `.exe`. Confira os direitos de redistribuição das famílias e as licenças dos modelos (o
`LEIA-ME.txt` do pacote lista as fontes).

**Conta do GitHub:** o repositório é de `valber99`. Se o `gh` estiver logado em outra conta, use
`GH_TOKEN="$(gh auth token --user valber99)"` em cada comando e faça o push com
`git -c credential.helper= -c "credential.helper=!gh auth git-credential" push`.

**Releases antigas:** até a 0.3.2 / 0.2.0 / 0.1.1 cada plugin tinha a sua release (`v*`, `familias-v*`, `dwg-v*`). Desde
a 0.4.0 os três procuram atualização só nas tags `v*`. Quem tem o Material Studio 0.3.2 recebe o aviso da 0.4.0 (ele já
lia `v*`), e o botão Sobre é único, então o aviso aparece para os três.

## 5. CI

`.github/workflows/tests.yml` roda a cada push:

- **core**: testes xUnit no Windows e no Linux.
- **addin**: compila as três versões e empacota (artefato `GBMaterialStudio`).
- **pyrevit-edition**: pytest da edição legada em pyRevit.
- **gbfam**: `cargo test` do motor em Rust (`tools/gbfam`).

## Limitações conhecidas

- O add-in e o instalador não são assinados (o Revit e o Windows avisam). Assinar exige um certificado de
  assinatura de código.
- O teste automático dentro do Revit não clica nas janelas; os comandos do Material Studio e do DWG Export continuam
  sendo testados à mão.
