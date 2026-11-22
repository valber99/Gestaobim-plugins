//! Busca hibrida: texto (e5->descricao + CLIP->imagem) ou imagem (CLIP), combinadas por RRF ponderada.
//! Os vetores estao normalizados, entao o produto escalar e a similaridade de cosseno.
//! Forca bruta em memoria: ~3,4 mil vetores de 512 floats (7 MB) respondem em milissegundos.

use crate::db::blob_to_vec;
use crate::embed::Models;
use anyhow::{bail, Result};
use rusqlite::Connection;
use std::collections::{HashMap, HashSet};

/// Ids mais proximos de `query` na coluna `col`, ja restritos a `allowed` (quando houver).
fn nearest(conn: &Connection, col: &str, query: &[f32], allowed: Option<&HashSet<i64>>, n: usize) -> Result<Vec<i64>> {
    // `col` vem so de constantes internas abaixo, nunca de entrada do usuario.
    let sql = format!("SELECT family_id, {col} FROM embeddings WHERE {col} IS NOT NULL");
    let mut st = conn.prepare(&sql)?;
    let mut scored: Vec<(i64, f32)> = st
        .query_map([], |r| Ok((r.get::<_, i64>(0)?, r.get::<_, Vec<u8>>(1)?)))?
        .filter_map(|r| r.ok())
        .filter(|(id, _)| allowed.map_or(true, |a| a.contains(id)))
        .map(|(id, blob)| {
            let v = blob_to_vec(&blob);
            (id, v.iter().zip(query).map(|(a, b)| a * b).sum::<f32>())
        })
        .collect();
    scored.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap());
    scored.truncate(n);
    Ok(scored.into_iter().map(|(id, _)| id).collect())
}

/// Ranking final: lista de (id, pontuacao RRF), do melhor para o pior.
pub fn rank(
    conn: &Connection,
    models: &mut Models,
    query: Option<&str>,
    image: Option<&[u8]>,
    allowed: Option<&HashSet<i64>>,
    top: usize,
) -> Result<Vec<(i64, f64)>> {
    if query.map_or(true, |q| q.trim().is_empty()) && image.is_none() {
        bail!("informe um texto ou uma imagem");
    }
    let pool_n = (top * 4).max(50);
    // (peso, lista). O CLIP e treinado em ingles: com consulta em portugues ele e um sinal fraco,
    // entao o e5 (multilingue, sobre a descricao da IA) pesa mais. Na busca por imagem o vetor
    // de imagem e a fonte principal e o texto CLIP so desempata.
    let mut lists: Vec<(f64, Vec<i64>)> = Vec::new();
    if let Some(q) = query.filter(|q| !q.trim().is_empty()) {
        lists.push((1.0, nearest(conn, "desc_vec", &models.e5("query: ", q)?, allowed, pool_n)?));
        let clip = models.clip_text(q)?;
        lists.push((0.4, nearest(conn, "img", &clip, allowed, pool_n)?));
        lists.push((0.4, nearest(conn, "clip_txt", &clip, allowed, pool_n)?));
    }
    if let Some(bytes) = image {
        let v = models.image(bytes)?;
        lists.push((1.0, nearest(conn, "img", &v, allowed, pool_n)?));
        lists.push((0.3, nearest(conn, "clip_txt", &v, allowed, pool_n)?));
    }

    // Reciprocal Rank Fusion ponderada
    let mut score: HashMap<i64, f64> = HashMap::new();
    for (weight, list) in lists {
        for (rank, id) in list.into_iter().enumerate() {
            *score.entry(id).or_default() += weight / (60.0 + rank as f64 + 1.0);
        }
    }
    let mut ranked: Vec<(i64, f64)> = score.into_iter().collect();
    ranked.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap());
    ranked.truncate(top);
    Ok(ranked)
}

pub fn run(conn: &Connection, query: Option<String>, image: Option<String>, top: usize) -> Result<()> {
    let mut models = Models::load()?;
    let bytes = match &image {
        Some(path) => Some(std::fs::read(path)?),
        None => None,
    };
    let ranked = rank(conn, &mut models, query.as_deref(), bytes.as_deref(), None, top)?;
    let mut info = conn.prepare(
        "SELECT f.name, f.rel_path, en.categoria FROM families f
           LEFT JOIN enrichments en ON en.family_id = f.id WHERE f.id = ?1",
    )?;
    for (i, (id, s)) in ranked.into_iter().enumerate() {
        let (name, rel, cat): (String, String, Option<String>) =
            info.query_row([id], |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)))?;
        println!("{:>2}. [{:.4}] #{id} {name}  ({})\n      {rel}", i + 1, s, cat.unwrap_or_else(|| "-".into()));
    }
    Ok(())
}
