# Como publicar uma versão

Passo a passo para gerar e publicar uma release do add-in.

## 1. Preparar

1. Altere `<Version>` em `src/GBMaterialStudio/GBMaterialStudio.csproj` (por exemplo, `0.3.2`). Esse número é o
   que o botão **Sobre** compara com a última release do GitHub.
2. Atualize o `CHANGELOG.md` com uma seção para a versão.
3. Rode os testes: `dotnet test tests/GBMaterialStudio.Core.Tests`.

## 2. Empacotar

```powershell
./build/package.ps1
```

O script compila para o Revit 2025, 2026 e 2027 e gera em `dist/`:

| Arquivo | Para quê |
|---|---|
| `GBMaterialStudio-<versão>.zip` e `.sha256` | Pacote com `Instalar.bat`, `Desinstalar.bat` e as três compilações |
| `GBMaterialStudio-Setup-<versão>.exe` e `.sha256` | Instalador (`build/SetupStub.cs`): confere o SHA-256, extrai o `.zip` e roda o `Install.ps1` |

O `.exe` recusa instalar com o Revit aberto e registra o que fez em `%TEMP%\gb-material-studio-setup.log`.

## 3. Testar no Revit

O código que usa a API do Revit só pode ser testado dentro do Revit.

1. Feche o Revit e instale o pacote (`Instalar.bat` ou o `.exe`).
2. Abra o Revit e confira: aba "Gestão BIM", painel "Materiais" com os seis botões.
3. Teste os fluxos alterados. Para o aviso de atualização, o botão **Sobre** consulta o GitHub; só aparece a
   bolinha quando a última release publicada é mais nova que a instalada.

## 4. Publicar

```powershell
git push                      # como valber99, ver a nota abaixo
gh release create v<versão> `
  dist/GBMaterialStudio-<versão>.zip dist/GBMaterialStudio-<versão>.zip.sha256 `
  dist/GBMaterialStudio-Setup-<versão>.exe dist/GBMaterialStudio-Setup-<versão>.exe.sha256 `
  --title "GB Material Studio <versão>" --notes-file notas.md
```

Nas notas, diga o que foi verificado em execução e o que não foi (por exemplo, quais versões do Revit foram
abertas), e lembre do aviso "Complemento não certificado" e do aviso do Windows sobre o `.exe`.

**Conta do GitHub:** o repositório é de `valber99`. Se o `gh` estiver logado em outra conta, use
`GH_TOKEN="$(gh auth token --user valber99)"` em cada comando e faça o push com
`git -c credential.helper= -c "credential.helper=!gh auth git-credential" push`.

## GB Famílias

Tag `familias-v<versão>` (a versão vem de `src/GBFamilias/GBFamilias.csproj`). O pacote é gerado na máquina de quem
publica porque leva o `gbfam.exe` e os modelos de IA, que não estão no repositório. O **catálogo** (banco com as
famílias) vai **dentro do instalador**; o servidor só entrega os `.rfa`:

```powershell
cargo build --release --manifest-path tools/gbfam/Cargo.toml     # o gbfam.exe
./build/package-familias.ps1                 # dist/GBFamilias-<versão>.zip e GBFamilias-Setup-<versão>.exe (~0,7 GB cada)
./build/package-catalog.ps1                  # dist/GBFamilias-Catalogo-<v>.zip (só o catálogo, alguns MB)
```

Os `.exe` dos três plugins são o instalador próprio `build/SetupStub.cs` (o IExpress travava com o tamanho do GB
Famílias), compilado pelo `csc` do .NET Framework que vem no Windows, com o `.zip` colado no fim. Ele confere o SHA-256
do pacote, recusa instalar com o Revit aberto e roda o `Install.ps1` (`/quiet` não pausa no fim).

Para testar a release instalada (e não uma compilação nova), feche o Revit, rode os `.exe` e depois
`./build/revit-selftest.ps1 -Year <ano> -SkipInstall`.

**Releases no GitHub:** o Material Studio lê a release marcada como *Latest*. Publique as do DWG Export e do GB Famílias
com `--latest=false` e a do Material Studio por último.

O pacote completo da biblioteca (`package-library.ps1`) **não é mais publicado**. Antes de publicar uma versão:
rode `dotnet test tests/GBFamilias.Core.Tests` e `cargo test --release` em `tools/gbfam`, confira o status do
servidor (saúde do serviço de arquivos) e teste a janela num Revit real (**inserir uma família que ainda não foi baixada**).
A versão do plugin precisa ser igual ou maior que o `min_plugin` do catálogo publicado.

Detalhes, parâmetros e limites em [`GB-FAMILIAS.md`](GB-FAMILIAS.md#publicar). Confira os direitos de redistribuição
das famílias e as licenças dos modelos (o `LEIA-ME.txt` do pacote lista as fontes).

## 5. CI

`.github/workflows/tests.yml` roda a cada push:

- **core**: testes xUnit no Windows e no Linux.
- **addin**: compila as três versões e empacota (artefato `GBMaterialStudio`).
- **pyrevit-edition**: pytest da edição legada em pyRevit.
- **gbfam**: `cargo test` do motor em Rust (`tools/gbfam`).

## Limitações conhecidas

- O add-in e o instalador não são assinados (o Revit e o Windows avisam). Assinar exige um certificado de
  assinatura de código.
- O teste automático dentro do Revit (`build/revit-selftest.ps1`) cobre o GB Famílias e o botão Sobre dos três plugins,
  nos Revit 2025, 2026 e 2027; os comandos do Material Studio e do DWG Export continuam sendo testados à mão.
