//! Embeddings locais em CPU (ONNX via fastembed): CLIP ViT-B/32 (imagem e texto) e multilingual-e5-small.

use crate::db::{vec_literal, DIM_CLIP, DIM_E5};
use anyhow::{anyhow, Result};
use fastembed::{
    EmbeddingModel, ImageEmbedding, ImageEmbeddingModel, ImageInitOptions, TextEmbedding, TextInitOptions,
};
use sqlx::{PgPool, Row};
use std::path::PathBuf;

pub struct Models {
    pub clip_img: ImageEmbedding,
    pub clip_txt: TextEmbedding,
    pub e5: TextEmbedding,
}

fn cache_dir() -> PathBuf {
    std::env::var("GBFAM_MODELS")
        .map(PathBuf::from)
        .unwrap_or_else(|_| PathBuf::from(".models"))
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

pub async fn run(pool: &PgPool, limit: i64) -> Result<()> {
    let rows = sqlx::query(
        "SELECT f.id, f.name, f.folder_category, f.folder_hint, p.png,
                e.descricao, e.categoria, e.tags, e.data
           FROM families f
           LEFT JOIN previews p ON p.family_id = f.id
           LEFT JOIN enrichments e ON e.family_id = f.id
          WHERE NOT EXISTS (SELECT 1 FROM embeddings m WHERE m.family_id = f.id)
          ORDER BY f.id LIMIT $1",
    )
    .bind(limit)
    .fetch_all(pool)
    .await?;
    if rows.is_empty() {
        println!("nada a embedar");
        return Ok(());
    }
    println!("{} familias para embedar", rows.len());
    let mut models = Models::load()?;

    let (mut ok, mut fail) = (0u32, 0u32);
    for (i, r) in rows.iter().enumerate() {
        let id: i64 = r.get("id");
        let name: String = r.get("name");
        let png: Option<Vec<u8>> = r.get("png");
        let descricao: Option<String> = r.get("descricao");
        let categoria: Option<String> = r.get("categoria");
        let tags: Option<Vec<String>> = r.get("tags");
        let folder: Option<String> = r.get("folder_category");
        let hint: Option<String> = r.get("folder_hint");
        let data: Option<serde_json::Value> = r.get("data");

        let friendly = data
            .as_ref()
            .and_then(|d| d.get("nome_amigavel"))
            .and_then(|v| v.as_str())
            .unwrap_or(&name)
            .to_string();
        let cat = categoria.clone().or(folder.clone()).unwrap_or_default();
        let tags_s = tags.clone().unwrap_or_default().join(", ");
        // CLIP aceita ~77 tokens: texto curto.
        let short = format!("{friendly}, {cat}, {}", tags.clone().unwrap_or_default().iter().take(6).cloned().collect::<Vec<_>>().join(", "));
        let passage = format!(
            "{friendly}. {} Categoria: {cat}. Tags: {tags_s}. Origem: {} {}",
            descricao.clone().unwrap_or_default(),
            folder.unwrap_or_default(),
            hint.unwrap_or_default()
        );

        let result: Result<(Option<Vec<f32>>, Vec<f32>, Vec<f32>)> = (|| {
            let img = match &png {
                Some(p) => models.image(p).ok(),
                None => None,
            };
            Ok((img, models.clip_text(&short)?, models.e5("passage: ", &passage)?))
        })();

        match result {
            Ok((img, ct, dv)) => {
                sqlx::query(
                    "INSERT INTO embeddings (family_id, img, clip_txt, desc_vec)
                     VALUES ($1, $2::text::vector, $3::text::vector, $4::text::vector)
                     ON CONFLICT (family_id) DO NOTHING",
                )
                .bind(id)
                .bind(img.as_deref().map(vec_literal))
                .bind(vec_literal(&ct))
                .bind(vec_literal(&dv))
                .execute(pool)
                .await?;
                ok += 1;
            }
            Err(e) => {
                fail += 1;
                eprintln!("falhou {name}: {e:#}");
            }
        }
        if (i + 1) % 50 == 0 {
            println!("  {}/{}", i + 1, rows.len());
        }
    }
    println!("embeddings: {ok} | falhas: {fail}");
    Ok(())
}
