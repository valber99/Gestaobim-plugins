//! Embeddings locais em CPU (ONNX via fastembed): CLIP ViT-B/32 (imagem e texto) e multilingual-e5-small.

use crate::db::{vec_to_blob, DIM_CLIP, DIM_E5};
use anyhow::{anyhow, Result};
use fastembed::{
    EmbeddingModel, ImageEmbedding, ImageEmbeddingModel, ImageInitOptions, TextEmbedding, TextInitOptions,
};
use rusqlite::{params, Connection};
use std::path::PathBuf;

pub struct Models {
    pub clip_img: ImageEmbedding,
    pub clip_txt: TextEmbedding,
    pub e5: TextEmbedding,
}

/// Onde ficam os modelos ONNX: GBFAM_MODELS; senao `models` ao lado do gbfam.exe (instalacao);
/// senao `.models` do projeto (desenvolvimento: target\release\..\..\.models); senao `models` ao lado do exe (baixa la).
fn cache_dir() -> PathBuf {
    if let Ok(custom) = std::env::var("GBFAM_MODELS") {
        return PathBuf::from(custom);
    }
    let exe_dir = std::env::current_exe()
        .ok()
        .and_then(|p| p.parent().map(|d| d.to_path_buf()))
        .unwrap_or_else(|| PathBuf::from("."));
    let installed = exe_dir.join("models");
    if installed.exists() {
        return installed;
    }
    let dev = exe_dir.join("..").join("..").join(".models");
    if dev.exists() {
        return dev;
    }
    installed
}

impl Models {
    pub fn load() -> Result<Self> {
        let dir = cache_dir();
        println!("carregando modelos (primeira vez baixa ~400 MB para {})...", dir.display());
        let clip_img = ImageEmbedding::try_new(
            ImageInitOptions::new(ImageEmbeddingModel::ClipVitB32).with_cache_dir(dir.clone()),
        )?;
        let clip_txt = TextEmbedding::try_new(
            TextInitOptions::new(EmbeddingModel::ClipVitB32).with_cache_dir(dir.clone()),
        )?;
        let e5 = TextEmbedding::try_new(
            TextInitOptions::new(EmbeddingModel::MultilingualE5Small).with_cache_dir(dir),
        )?;
        Ok(Self { clip_img, clip_txt, e5 })
    }

    pub fn image(&mut self, png: &[u8]) -> Result<Vec<f32>> {
        let mut v = self.clip_img.embed_bytes(&[png], None)?;
        check(v.pop(), DIM_CLIP)
    }
    pub fn clip_text(&mut self, text: &str) -> Result<Vec<f32>> {
        let mut v = self.clip_txt.embed(vec![text], None)?;
        check(v.pop(), DIM_CLIP)
    }
    /// `prefix` = "query: " na busca, "passage: " na indexacao (convencao do e5).
    pub fn e5(&mut self, prefix: &str, text: &str) -> Result<Vec<f32>> {
        let mut v = self.e5.embed(vec![format!("{prefix}{text}")], None)?;
        check(v.pop(), DIM_E5)
    }
}

fn check(v: Option<Vec<f32>>, dim: usize) -> Result<Vec<f32>> {
    let v = v.ok_or_else(|| anyhow!("modelo nao retornou vetor"))?;
    if v.len() != dim {
        return Err(anyhow!("dimensao inesperada: {} (esperado {dim})", v.len()));
    }
    Ok(normalize(v))
}

fn normalize(mut v: Vec<f32>) -> Vec<f32> {
    let n = v.iter().map(|x| x * x).sum::<f32>().sqrt();
    if n > 0.0 {
        v.iter_mut().for_each(|x| *x /= n);
    }
    v
}

struct Row {
    id: i64,
    name: String,
    folder: Option<String>,
    hint: Option<String>,
    png: Option<Vec<u8>>,
    descricao: Option<String>,
    categoria: Option<String>,
    tags: Vec<String>,
    friendly: Option<String>,
}

pub async fn run(conn: &Connection, limit: i64) -> Result<()> {
    let rows: Vec<Row> = {
        let mut st = conn.prepare(
            "SELECT f.id, f.name, f.folder_category, f.folder_hint, p.png,
                    e.descricao, e.categoria, e.tags, e.data
               FROM families f
               LEFT JOIN previews p ON p.family_id = f.id
               LEFT JOIN enrichments e ON e.family_id = f.id
              WHERE NOT EXISTS (SELECT 1 FROM embeddings m WHERE m.family_id = f.id)
              ORDER BY f.id LIMIT ?1",
        )?;
        let it = st.query_map([limit], |r| {
            let tags: Option<String> = r.get(7)?;
            let data: Option<String> = r.get(8)?;
            let friendly = data
                .and_then(|d| serde_json::from_str::<serde_json::Value>(&d).ok())
                .and_then(|v| v.get("nome_amigavel").and_then(|x| x.as_str()).map(String::from));
            Ok(Row {
                id: r.get(0)?,
                name: r.get(1)?,
                folder: r.get(2)?,
                hint: r.get(3)?,
                png: r.get(4)?,
                descricao: r.get(5)?,
                categoria: r.get(6)?,
                tags: tags.and_then(|t| serde_json::from_str(&t).ok()).unwrap_or_default(),
                friendly,
            })
        })?;
        it.collect::<std::result::Result<_, _>>()?
    };
    if rows.is_empty() {
        println!("nada a embedar");
        return Ok(());
    }
    println!("{} familias para embedar", rows.len());
    let mut models = Models::load()?;

    let (mut ok, mut fail) = (0u32, 0u32);
    conn.execute_batch("BEGIN")?;
    for (i, r) in rows.iter().enumerate() {
        let friendly = r.friendly.clone().unwrap_or_else(|| r.name.clone());
        let cat = r.categoria.clone().or(r.folder.clone()).unwrap_or_default();
        // CLIP aceita ~77 tokens: texto curto.
        let short = format!("{friendly}, {cat}, {}", r.tags.iter().take(6).cloned().collect::<Vec<_>>().join(", "));
        let passage = format!(
            "{friendly}. {} Categoria: {cat}. Tags: {}. Origem: {} {}",
            r.descricao.clone().unwrap_or_default(),
            r.tags.join(", "),
            r.folder.clone().unwrap_or_default(),
            r.hint.clone().unwrap_or_default()
        );

        let result: Result<(Option<Vec<f32>>, Vec<f32>, Vec<f32>)> = (|| {
            let img = match &r.png {
                Some(p) => models.image(p).ok(),
                None => None,
            };
            Ok((img, models.clip_text(&short)?, models.e5("passage: ", &passage)?))
        })();

        match result {
            Ok((img, ct, dv)) => {
                conn.execute(
                    "INSERT OR IGNORE INTO embeddings (family_id, img, clip_txt, desc_vec) VALUES (?1,?2,?3,?4)",
                    params![r.id, img.as_deref().map(vec_to_blob), vec_to_blob(&ct), vec_to_blob(&dv)],
                )?;
                ok += 1;
            }
            Err(e) => {
                fail += 1;
                eprintln!("falhou {}: {e:#}", r.name);
            }
        }
        if (i + 1) % 100 == 0 {
            conn.execute_batch("COMMIT; BEGIN")?;
            println!("  {}/{}", i + 1, rows.len());
        }
    }
    conn.execute_batch("COMMIT")?;
    println!("embeddings: {ok} | falhas: {fail}");
    Ok(())
}
