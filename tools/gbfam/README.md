# gbfam — banco de famílias Revit

CLI em Rust que lê os `.rfa` de `..\familias`, extrai preview e metadados, gera descrição/tags com o
**Claude Haiku 5.5**, calcula embeddings locais (CPU) e grava tudo em **um único arquivo SQLite**
(`gbfam.sqlite`) para busca por texto ou por imagem. Sem servidor de banco.

## Pipeline

```
ingest  →  enrich  →  embed  →  search
  │          │          │
  │          │          └─ CLIP ViT-B/32 (imagem e texto, 512d) + multilingual-e5-small (descrição, 384d)
  │          └─ Claude Haiku 5.5 (visão): descrição, categoria, tags, materiais, cores, estilo
  └─ OLE2: preview (PNG ou JPEG→PNG), BasicFileInfo, PartAtom; SHA-256 para deduplicar
```

Cada etapa é **retomável**: roda só o que ainda falta. Pode interromper e continuar.

Os vetores ficam como BLOB (f32 normalizado) e a busca é por força bruta em memória
(~3,4 mil vetores = 7 MB, milissegundos). Se a biblioteca crescer para centenas de milhares de
famílias, vale trocar por um índice ANN (ex.: extensão sqlite-vec).

## Preparação

1. A chave do Claude é lida da variável `key` no arquivo `.env` (informe com `--env` ou use `ANTHROPIC_API_KEY`).
2. `cargo build --release` → `target\release\gbfam.exe`
3. O banco é criado sozinho em `gbfam.sqlite` (mude com `--db` ou `GBFAM_DB`).

## Uso

```powershell
gbfam inspect                    # teste de extração, sem tocar no banco
gbfam ingest                     # varre ..\familias
gbfam enrich --limit 50          # piloto de 50; depois sem --limit (--concurrency 16 é mais rápido)
gbfam embed                      # 1ª vez baixa ~400 MB de modelos para .models\
gbfam search "cadeira de escritório"
gbfam search --image foto.png
gbfam stats
```

Tabelas: `families`, `previews`, `enrichments`, `embeddings`. Para consultar à mão use qualquer
cliente SQLite (DB Browser for SQLite, `sqlite3`, etc.).

## Famílias sem preview (~1%)

```powershell
gbfam missing --out missing.json
# No Revit: rodar tools\revit_gerar_previews.py (abre cada .rfa, exporta vista 3D, NÃO salva o original)
gbfam import-previews --dir previews_gerados
gbfam enrich ; gbfam embed
```

## O que a extração encontrou em ..\familias (3.856 .rfa válidos)

| | |
|---|---|
| Famílias únicas | 3.368 (485 duplicatas por conteúdo idêntico) |
| Com preview | 3.328 (98,8%): ~65% PNG, ~34% JPEG (Revit antigo) |
| Versão do Revit | 1.480 |
| Categoria Revit (PartAtom) | 654 — o stream só existe em ~17% dos arquivos |
| Ilegíveis | 3 |
| Ignorados | backups `nome.0001.rfa` |

As miniaturas têm ~128 px. Funciona para o CLIP (que usa 224 px), mas com menos detalhe.

## Qualidade da busca

O CLIP foi treinado em inglês, então consultas em português dependem sobretudo do e5 sobre a
descrição gerada (peso 1,0 contra 0,4 do CLIP). Funciona bem para objetos comuns ("vaso de planta",
"lavatório") e é irregular para termos específicos. Caminhos de melhoria: traduzir a consulta para
inglês antes do CLIP, reranquear os top-N com o Haiku, ou gerar previews maiores pelo Revit.

## Privacidade

`BasicFileInfo` grava usuário e caminhos locais; o gbfam guarda só `Format`, `Revit Build` e `Worksharing`.
Para a API do Claude vão: miniatura, nome do arquivo, caminho relativo e metadados do Revit.

## Migração antiga

`examples/pg_to_sqlite.rs` foi usado uma vez para trazer os dados de um Postgres de teste para o SQLite.
Não é necessário no uso normal.
