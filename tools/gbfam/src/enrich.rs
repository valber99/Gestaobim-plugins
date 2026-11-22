//! Gera descricao, categoria e tags com o Claude (visao) a partir de imagem + nome + dados do Revit.

use anyhow::{anyhow, bail, Context, Result};
use base64::Engine;
use futures::StreamExt;
use indicatif::{ProgressBar, ProgressStyle};
use std::sync::atomic::{AtomicU64, Ordering};

/// Tokens gastos nesta execucao (da resposta da API: `usage.input_tokens` / `usage.output_tokens`).
static IN_TOKENS: AtomicU64 = AtomicU64::new(0);
static OUT_TOKENS: AtomicU64 = AtomicU64::new(0);
/// US$ por milhao de tokens do Haiku 5.5 (prompts de ate 100 mil tokens).
const PRICE_IN: f64 = 0.10;
const PRICE_OUT: f64 = 0.50;
use serde_json::{json, Value};
use rusqlite::{params, Connection};
use std::time::Duration;

pub const MODEL: &str = "claude-haiku-5-5";
const API_URL: &str = "https://api.anthropic.com/v1/messages";

const SYSTEM: &str = "Voce cataloga familias do Revit (.rfa) para uma biblioteca BIM brasileira. \
Receba o nome do arquivo, a pasta de origem, metadados do Revit e (quando houver) a miniatura da familia. \
Responda APENAS com um objeto JSON valido, sem markdown, em portugues do Brasil, com as chaves: \
\"nome_amigavel\" (string curta), \"descricao\" (2 a 3 frases objetivas sobre o que e o objeto, sua forma, \
material e uso), \"categoria\" (TIPO DE OBJETO, ex.: Portas, Janelas, Mobiliario, Iluminacao, Hidraulica, Eletrica, Vegetacao, \
Equipamentos, Anotacao, ...; a pasta de origem costuma ser o AMBIENTE, como Sala ou Cozinha, e e apenas uma dica: \
nunca copie o nome da pasta como categoria), \
\"subcategoria\" (string, ex.: Sofas, Mesas de jantar),\"tags\" (8 a 15 palavras-chave minusculas, sem repeticao, uteis para busca), \
\"materiais\" (lista), \"cores\" (lista), \"estilo\" (string ou null), \"ambientes\" (lista, ex.: sala, cozinha), \
\"confianca\" (numero de 0 a 1). Use SEMPRE a acentuacao correta do portugues em todos os textos (ex.: Mobiliário, Iluminação, Elétrica), \
com a categoria sempre no singular de grupo e com inicial maiuscula. Nao invente marca ou dimensoes que nao estejam nos dados; \
se algo for incerto, use null ou lista vazia e reduza a confianca.";

pub fn api_key() -> Result<String> {
    for var in ["ANTHROPIC_API_KEY", "key", "KEY"] {
        if let Ok(v) = std::env::var(var) {
            let v = v.trim().to_string();
            if !v.is_empty() {
                return Ok(v);
            }
        }
    }
    bail!("chave da API nao encontrada (defina ANTHROPIC_API_KEY ou a variavel `key` no arquivo .env)")
}

struct Item {
    id: i64,
    name: String,
    rel_path: String,
    folder_category: Option<String>,
    folder_hint: Option<String>,
    revit_version: Option<String>,
    revit_category: Option<String>,
    part_atom: Option<Value>,
    png: Option<Vec<u8>>,
}

pub async fn run(conn: &Connection, limit: i64, concurrency: usize) -> Result<()> {
    let key = api_key()?;
    let items: Vec<Item> = {
        let mut st = conn.prepare(
            "SELECT f.id, f.name, f.rel_path, f.folder_category, f.folder_hint, f.revit_version,
                    f.revit_category, f.part_atom, p.png
               FROM families f
               LEFT JOIN previews p ON p.family_id = f.id
              WHERE NOT EXISTS (SELECT 1 FROM enrichments e WHERE e.family_id = f.id)
              ORDER BY f.id
              LIMIT ?1",
        )?;
        let rows = st.query_map([limit], |r| {
            let part_atom: Option<String> = r.get(7)?;
            Ok(Item {
                id: r.get(0)?,
                name: r.get(1)?,
                rel_path: r.get(2)?,
                folder_category: r.get(3)?,
                folder_hint: r.get(4)?,
                revit_version: r.get(5)?,
                revit_category: r.get(6)?,
                part_atom: part_atom.and_then(|s| serde_json::from_str(&s).ok()),
                png: r.get(8)?,
            })
        })?;
        rows.collect::<std::result::Result<_, _>>()?
    };

    println!("{} familias para enriquecer com {MODEL}", items.len());
    let pb = ProgressBar::new(items.len() as u64);
    pb.set_style(ProgressStyle::with_template("{bar:40} {pos}/{len} {msg}").unwrap());
    let client = reqwest::Client::builder().timeout(Duration::from_secs(120)).build()?;

    let mut stream = futures::stream::iter(items)
        .map(|it| {
            let client = client.clone();
            let key = key.clone();
            async move {
                let id = it.id;
                let name = it.name.clone();
                (id, name, describe(&client, &key, &it).await)
            }
        })
        .buffer_unordered(concurrency.max(1));

    let (mut ok, mut fail) = (0u32, 0u32);
    while let Some((id, name, res)) = stream.next().await {
        pb.inc(1);
        match res {
            Ok(data) => {
                let descricao = data.get("descricao").and_then(|v| v.as_str()).map(String::from);
                let categoria = data.get("categoria").and_then(|v| v.as_str()).map(String::from);
                let tags: Vec<String> = data
                    .get("tags")
                    .and_then(|v| v.as_array())
                    .map(|a| a.iter().filter_map(|t| t.as_str().map(|s| s.to_lowercase())).collect())
                    .unwrap_or_default();
                conn.execute(
                    "INSERT OR IGNORE INTO enrichments (family_id, model, data, descricao, categoria, tags)
                     VALUES (?1,?2,?3,?4,?5,?6)",
                    params![id, MODEL, data.to_string(), descricao, categoria, serde_json::to_string(&tags)?],
                )?;
                ok += 1;
            }
            Err(e) => {
                fail += 1;
                pb.println(format!("falhou {name}: {e:#}"));
            }
        }
    }
    pb.finish_and_clear();
    println!("enriquecidas: {ok} | falhas: {fail} (rode de novo para repetir as falhas)");
    let (tin, tout) = (IN_TOKENS.load(Ordering::Relaxed), OUT_TOKENS.load(Ordering::Relaxed));
    let calls = (ok + fail).max(1) as f64;
    let cost = tin as f64 / 1e6 * PRICE_IN + tout as f64 / 1e6 * PRICE_OUT;
    println!(
        "uso da API: {tin} tokens de entrada + {tout} de saida | por familia: {:.0} + {:.0} | custo: US$ {cost:.4} (US$ {:.5} por familia; {} familias custariam ~US$ {:.2})",
        tin as f64 / calls, tout as f64 / calls, cost / calls, 25_000, cost / calls * 25_000.0
    );
    Ok(())
}

async fn describe(client: &reqwest::Client, key: &str, it: &Item) -> Result<Value> {
    let mut facts = format!(
        "Nome do arquivo: {}\nCaminho relativo: {}\nPasta de categoria: {}\nSubpastas: {}\nVersao do Revit: {}\nCategoria no Revit: {}\n",
        it.name,
        it.rel_path,
        it.folder_category.as_deref().unwrap_or("-"),
        it.folder_hint.as_deref().unwrap_or("-"),
        it.revit_version.as_deref().unwrap_or("-"),
        it.revit_category.as_deref().unwrap_or("-"),
    );
    if let Some(pa) = &it.part_atom {
        if let Some(texts) = pa.get("texts").and_then(|t| t.as_array()) {
            facts.push_str("Metadados PartAtom:\n");
            for t in texts.iter().take(40) {
                facts.push_str(&format!(
                    "- {}: {}\n",
                    t.get("campo").and_then(|v| v.as_str()).unwrap_or(""),
                    t.get("valor").and_then(|v| v.as_str()).unwrap_or("")
                ));
            }
        }
    }
    if it.png.is_none() {
        facts.push_str("(Sem miniatura: descreva apenas pelos dados acima e reduza a confianca.)\n");
    }

    let mut content = Vec::new();
    if let Some(png) = &it.png {
        content.push(json!({
            "type": "image",
            "source": {
                "type": "base64",
                "media_type": "image/png",
                "data": base64::engine::general_purpose::STANDARD.encode(png),
            }
        }));
    }
    content.push(json!({ "type": "text", "text": facts }));

    let body = json!({
        "model": MODEL,
        // O Haiku 5.5 emite um bloco `thinking` antes do texto e ele conta no limite.
        "max_tokens": 4096,
        "system": SYSTEM,
        "messages": [{ "role": "user", "content": content }],
    });

    let mut delay = Duration::from_secs(2);
    for attempt in 0..5 {
        let resp = client
            .post(API_URL)
            .header("x-api-key", key)
            .header("anthropic-version", "2023-06-01")
            .json(&body)
            .send()
            .await;
        let resp = match resp {
            Ok(r) => r,
            Err(e) if attempt < 4 => {
                tokio::time::sleep(delay).await;
                delay *= 2;
                let _ = e;
                continue;
            }
            Err(e) => return Err(e.into()),
        };
        let status = resp.status();
        if status.as_u16() == 429 || status.as_u16() == 529 || status.is_server_error() {
            if attempt < 4 {
                tokio::time::sleep(delay).await;
                delay *= 2;
                continue;
            }
        }
        let v: Value = resp.json().await.context("resposta nao e JSON")?;
        IN_TOKENS.fetch_add(v["usage"]["input_tokens"].as_u64().unwrap_or(0), Ordering::Relaxed);
        OUT_TOKENS.fetch_add(v["usage"]["output_tokens"].as_u64().unwrap_or(0), Ordering::Relaxed);
        if !status.is_success() {
            let msg = v["error"]["message"].as_str().unwrap_or("erro desconhecido");
            bail!("API {status}: {msg}");
        }
        if v["stop_reason"].as_str() == Some("max_tokens") {
            bail!("resposta cortada por max_tokens");
        }
        // Junta apenas os blocos de texto (ignora `thinking`).
        let text: String = v["content"]
            .as_array()
            .map(|a| {
                a.iter()
                    .filter(|b| b["type"].as_str() == Some("text"))
                    .filter_map(|b| b["text"].as_str())
                    .collect::<Vec<_>>()
                    .join("")
            })
            .unwrap_or_default();
        if text.is_empty() {
            return Err(anyhow!("resposta sem bloco de texto"));
        }
        return parse_json_object(&text);
    }
    bail!("esgotou as tentativas")
}

fn parse_json_object(text: &str) -> Result<Value> {
    let start = text.find('{').ok_or_else(|| anyhow!("sem JSON na resposta"))?;
    let end = text.rfind('}').ok_or_else(|| anyhow!("JSON incompleto"))?;
    let v: Value = serde_json::from_str(&text[start..=end]).context("JSON invalido")?;
    if v.get("descricao").and_then(|d| d.as_str()).is_none() {
        bail!("JSON sem o campo descricao");
    }
    Ok(v)
}
