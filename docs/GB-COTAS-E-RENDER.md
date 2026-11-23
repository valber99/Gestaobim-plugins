# GB Cotas e GB Render (IA)

Dois plugins da aba **Gestão BIM**, desde a 0.5.0, que vêm no instalador único junto com os outros. O **GB Cotas**
funciona sem IA. O **GB Render** precisa de um modelo de imagem para gerar o render, mas exporta as camadas sem IA. O
plano, as decisões e o que foi medido estão em [PLANO-GB-IA.md](PLANO-GB-IA.md).

## Guia do usuário

### Configurar a IA (painel IA → Configurar IA)

A janela é a mesma para os dois plugins. Você escolhe como pagar a IA:

- **Chave de API** (Gemini, Claude ou OpenAI). Fica guardada cifrada pelo Windows (DPAPI), em
  `%APPDATA%\GestaoBIM\Ai\keys.dat`, e só abre na sua conta deste computador.
- **Assinatura** (Claude Code, ChatGPT Codex ou Antigravity). O navegador abre para você entrar. Na primeira vez, a
  janela avisa que usar a assinatura fora do produto do provedor **pode ir contra os termos de uso dele** e levar ao
  bloqueio da conta. Só continua se você aceitar. A chave de API continua disponível como alternativa.

Depois, escolha o **modelo de planejamento** (GB Cotas) e o **modelo de imagem** (GB Render). Use **Testar conexão**
para conferir. A IA passa por um gateway local, o CLIProxyAPI. Ele roda só neste computador (`127.0.0.1`), com uma senha
nova a cada sessão do Revit, e fecha junto com o Revit.

### GB Cotas (painel Cotas)

1. Abra uma **planta** (de piso, de forro, estrutural ou de área). Para cotar só uma parte, selecione as paredes antes.
2. Clique em **Cotar vista** (painel Cotas). A janela mostra as cadeias propostas (interna, intermediária e externa de cada lado),
   com o número de segmentos de cada uma.
3. Opcional: escreva um pedido para a IA (por exemplo, "cote as fachadas em três linhas e os banheiros por dentro") e
   clique em **Planejar** (sem modelo escolhido, a opção é **Sem IA**, pelas regras). A IA só escolhe referências da lista que o plugin montou. Uma cadeia com referência
   que não existe, ou com referências que não são paralelas, é recusada e aparece com o motivo.
4. Marque as cadeias que quer e clique em **Criar cotas**.

O que o plugin garante:

- As cotas são **nativas do Revit** e associadas às faces. Se a parede mudar, a cota acompanha.
- As linhas não se sobrepõem entre si nem a tags, textos ou cotas que já estavam na vista. Quando há colisão, a linha
  é afastada para fora.
- Tudo vira **uma operação só**: um Ctrl+Z desfaz.
- Cada cota é lida de volta depois de criada. Se o Revit medir algo diferente do plano, aquela cadeia é desfeita e
  aparece no aviso final.
- O relatório da última cotagem fica em `%APPDATA%\GestaoBIM\Cotas\autodim_audit.json`.

**Perfil de regras** (botão **Regras…** na janela de cotas): um JSON em `%APPDATA%\GestaoBIM\Cotas\perfil-regras.json`,
com padrão baseado na NBR 6492. Nele ficam os afastamentos em mm de papel (primeira linha a 10 mm, linhas a cada
7 mm), a altura do texto, os lados com as três linhas e os lados só com a externa, e a faixa da fachada. Se o arquivo
tiver erro, o plugin diz qual. Apague o arquivo para voltar ao padrão.

Limitações:

- **Paredes-cortina** e paredes inclinadas em relação às demais ficam de fora. Uma planta só com paredes-cortina é
  recusada com esse motivo.
- Planta girada: as cotas seguem a direção da maior parte das paredes.
- **Cortes e elevações** ainda não são cotados: a cota vertical precisa de níveis e lajes, que o extrator não lê. O
  plugin recusa a vista e diz o motivo.
- Vínculos (RVT dentro do projeto) ainda não são cotados.

### GB Render (painel Render → Render com IA)

1. Abra uma **vista 3D**, perspectiva (câmera) ou isométrica. Numa perspectiva, **ligue "Recortar vista"** nas
   propriedades e ajuste o recorte. Sem recorte, o Revit usa um enquadramento próprio que a API não informa, e o
   plugin recusa a vista com esse aviso.
2. Escolha a qualidade: Rascunho 1K, Padrão 2K ou Alta 4K. Escreva o pedido, escolha o fundo (céu e entorno) e a
   pasta de saída.
3. **Gerar camadas (sem IA)**: grava `clay.png`, `color_id.png` (uma cor por objeto), `depth.png` (profundidade em
   16 bits), `background.png` (céu, entorno e prédio), o `manifest.json` (o que é cada cor: categoria, família, tipo,
   material, posição na tela) e o `render_bundle.zip` com tudo conferido.
4. **Gerar render com IA**: envia as camadas e o manifesto ao modelo de imagem. O render e o `render_report.json`
   (modelo, hashes, sem chave nem senha) ficam na pasta.
5. **Editar um objeto do render**: selecione objetos no Revit e descreva a mudança (por exemplo, "trocar o couro por
   tecido azul"). Vai junto a máscara só desses objetos. O plugin confere se a imagem mudou fora da máscara e avisa
   se mudou.

O que vai para fora: as imagens das camadas e o manifesto. Marque **Não enviar fabricante e modelo das famílias**
para tirar esses campos do manifesto. Nada é enviado sem você clicar em gerar ou editar.

Limitações:

- Vidros com mais de 50% de transparência não entram nas camadas: o modelo vê o que está atrás deles.
- O render é do modelo de imagem. As camadas prendem a geometria, mas um modelo pode deslocar detalhes.

## Arquitetura

```
src/GBAi.Shared      gateway (sobe, confere e para o CLIProxyAPI), cliente HTTP, configuracoes, DPAPI, logins OAuth
src/GBAi.Revit       janela Configurar IA, painel IA e o botao unico (compilado dentro dos dois plugins)
src/GBCotas.Core     modelo, perfil de regras, validador do plano da IA, planejador por regras, solver de colisao
src/GBCotas          extrator de referencias (API do Revit), gravacao das cotas, janela, autoteste
src/GBRender.Core    camera, rasterizador com z-buffer, camadas, PNG, manifesto, pacote, prompts, conferencia da edicao
src/GBRender         extrator 3D (CustomExporter), janela, autoteste
```

- **A IA nunca vê coordenadas que possa mudar.** Ela recebe ids curtos (`r001`…) e devolve um plano JSON. O validador
  (`PlanValidator`) é a fronteira contra injeção de prompt: confere esquema, ids, direção e paralelismo. A geometria
  sai do solver, não da IA.
- **As camadas do render saem do rasterizador próprio**, não do `ExportImage`. Assim os quatro passes ficam alinhados
  pixel a pixel e a máscara não tem borda suavizada (cor que não existe no manifesto).
- **O gateway é travado.** O plugin só executa o `cli-proxy-api.exe` com o SHA-256 de `ProxyPin`. Ele gera o
  `config.yaml` a cada sessão e o confere antes de subir (`GatewayConfig.Verify`). Depois confere que o gateway
  responde 401 sem a senha. O processo fica num Job Object e morre com o Revit.

### Testes

| O quê | Como |
|---|---|
| Núcleos (sem Revit) | `dotnet test tests/GBAi.Shared.Tests`, `tests/GBCotas.Core.Tests`, `tests/GBRender.Core.Tests` |
| Gateway real | `GBAi.Shared.Tests` sobe o binário de `build/cache/cliproxy-x` se existir: 401/200/413, recusa na rede local, gerenciamento, ACL, dois gateways ao mesmo tempo, exe não aprovado |
| Dentro do Revit | `build/revit-selftest-ia.ps1 -Plugin Cotas\|Render -Year 2025 -Models a.rvt, b.rvt` (ver [RELEASE.md](RELEASE.md#3-testar-no-revit)) |

### Variáveis de ambiente

| Variável | Para quê |
|---|---|
| `GBAI_HOME` | Redireciona as pastas do gateway e das configurações (testes) |
| `GBAI_PROXY_EXE` | Outro caminho para o `cli-proxy-api.exe` (o SHA-256 continua exigido) |
| `GB_COTAS_HOME` | Outra pasta para o perfil de regras e o relatório |
| `GB_AI_NO_UPDATE_CHECK` | Não consultar versão nova |
| `GB_COTAS_SELFTEST`, `GB_COTAS_SELFTEST_MODELS` | Autoteste das cotas no Revit |
| `GB_RENDER_SELFTEST`, `GB_RENDER_SELFTEST_MODELS` | Autoteste do render no Revit |
