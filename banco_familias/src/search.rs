//! Busca hibrida: texto (CLIP->imagem + e5->descricao) ou imagem (CLIP), combinadas por RRF.

use crate::db::vec_literal;
use crate::embed::Models;
use anyhow::Result;
use sqlx::{PgPool, Row};
use std::collections::HashMap;

type Hit = (i64, String, String, Option<String>);

async fn nearest(pool: &PgPool, col: &str, vec: &[f32], n: i64) -> Result<Vec<Hit>> {
    // `col` vem so de constantes internas abaixo, nunca de entrada do usuario.
    let sql = format!(
        "SELECT f.id, f.name, f.rel_path, en.categoria
           FROM embeddings m
           JOIN families f ON f.id = m.family_id
           LEFT JOIN enrichments en ON en.family_id = f.id
          WHERE m.{col} IS NOT NULL
          ORDER BY m.{col} <=> $1::text::vector
          LIMIT $2"
    );
    let rows = sqlx::query(sqlx::AssertSqlSafe(sql)).bind(vec_literal(vec)).bind(n).fetch_all(pool).await?;
    Ok(rows
        .into_iter()
        .map(|r| (r.get(0), r.get(1), r.get(2), r.get(3)))
        .collect())
}

pub async fn run(pool: &PgPool, query: Option<String>, image: Option<String>, top: usize) -> Result<()> {
    let mut models = Models::load()?;
    let pool_n = (top * 4).max(20) as i64;
    let mut lists: Vec<Vec<Hit>> = Vec::new();

    if let Some(q) = &query {
        lists.push(nearest(pool, "img", &models.clip_text(q)?, pool_n).await?);
        lists.push(nearest(pool, "desc_vec", &models.e5("query: ", q)?, pool_n).await?);
    }
    if let Some(path) = &image {
        let bytes = std::fs::read(path)?;
        let v = models.image(&bytes)?;
        lists.push(nearest(pool, "img", &v, pool_n).await?);
        lists.push(nearest(pool, "clip_txt", &v, pool_n).await?);
    }

    // Reciprocal Rank Fusion
    let mut score: HashMap<i64, (f64, Hit)> = HashMap::new();
    for list in lists {
        for (rank, hit) in list.into_iter().enumerate() {
            let s = 1.0 / (60.0 + rank as f64 + 1.0);
            score.entry(hit.0).and_modify(|e| e.0 += s).or_insert((s, hit));
        }
    }
    let mut ranked: Vec<_> = score.into_values().collect();
    ranked.sort_by(|a, b| b.0.partial_cmp(&a.0).unwrap());
    for (i, (s, (id, name, rel, cat))) in ranked.into_iter().take(top).enumerate() {
        println!("{:>2}. [{:.4}] #{id} {name}  ({})\n      {rel}", i + 1, s, cat.unwrap_or_else(|| "-".into()));
    }
    Ok(())
}

