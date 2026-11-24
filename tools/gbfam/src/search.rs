//! Busca hibrida: texto (e5->descricao + CLIP->imagem) ou imagem (CLIP), combinadas por RRF ponderada.
//! Os vetores estao normalizados, entao o produto escalar e a similaridade de cosseno.
//! Os vetores sao carregados uma vez em buffers contiguos em RAM (VectorIndex) para responder em milissegundos
//! mesmo com dezenas de milhares de familias.

use crate::db::{DIM_CLIP, DIM_E5};
use crate::embed::Models;
use anyhow::{bail, Result};
use rusqlite::Connection;
use std::collections::{HashMap, HashSet};

pub struct ColumnIndex {
    dim: usize,
    ids: Vec<i64>,
    data: Vec<f32>,
}

impl ColumnIndex {
    fn new(dim: usize) -> Self {
        Self { dim, ids: Vec::new(), data: Vec::new() }
    }

    fn push_blob(&mut self, id: i64, blob: &[u8]) {
        if blob.len() != self.dim * 4 {
            return;
        }
        self.ids.push(id);
        self.data.reserve(self.dim);
        for c in blob.chunks_exact(4) {
            self.data.push(f32::from_le_bytes([c[0], c[1], c[2], c[3]]));
        }
    }

    fn nearest(&self, query: &[f32], allowed: Option<&HashSet<i64>>, n: usize) -> Vec<i64> {
        if self.ids.is_empty() || query.len() != self.dim || n == 0 {
            return Vec::new();
        }
        let mut scored: Vec<(i64, f32)> = Vec::with_capacity(self.ids.len());
        for (&id, vec) in self.ids.iter().zip(self.data.chunks_exact(self.dim)) {
            if let Some(a) = allowed {
                if !a.contains(&id) {
                    continue;
                }
            }
            let s: f32 = vec.iter().zip(query).map(|(a, b)| a * b).sum();
            if !s.is_nan() {
                scored.push((id, s));
            }
        }
        if scored.len() > n {
            scored.select_nth_unstable_by(n, |a, b| b.1.total_cmp(&a.1));
            scored.truncate(n);
        }
        scored.sort_unstable_by(|a, b| b.1.total_cmp(&a.1));
        scored.into_iter().map(|(id, _)| id).collect()
    }
}

pub struct VectorIndex {
    pub count: i64,
    img: ColumnIndex,
    clip_txt: ColumnIndex,
    desc_vec: ColumnIndex,
}

impl VectorIndex {
    pub fn load(conn: &Connection) -> Result<Self> {
        let mut img = ColumnIndex::new(DIM_CLIP);
        let mut clip_txt = ColumnIndex::new(DIM_CLIP);
        let mut desc_vec = ColumnIndex::new(DIM_E5);
        let mut count = 0i64;

        let mut st = conn.prepare("SELECT family_id, img, clip_txt, desc_vec FROM embeddings")?;
        let mut rows = st.query([])?;
        while let Some(r) = rows.next()? {
            count += 1;
            let id: i64 = r.get(0)?;
            if let Some(b) = r.get::<_, Option<Vec<u8>>>(1)? {
                img.push_blob(id, &b);
            }
            if let Some(b) = r.get::<_, Option<Vec<u8>>>(2)? {
                clip_txt.push_blob(id, &b);
            }
            if let Some(b) = r.get::<_, Option<Vec<u8>>>(3)? {
                desc_vec.push_blob(id, &b);
            }
        }
        Ok(Self { count, img, clip_txt, desc_vec })
    }
}

/// Ranking final: lista de (id, pontuacao RRF), do melhor para o pior.
pub fn rank(
    index: &VectorIndex,
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
        lists.push((1.0, index.desc_vec.nearest(&models.e5("query: ", q)?, allowed, pool_n)));
        let clip = models.clip_text(q)?;
        lists.push((0.4, index.img.nearest(&clip, allowed, pool_n)));
        lists.push((0.4, index.clip_txt.nearest(&clip, allowed, pool_n)));
    }
    if let Some(bytes) = image {
        let v = models.image(bytes)?;
        lists.push((1.0, index.img.nearest(&v, allowed, pool_n)));
        lists.push((0.3, index.clip_txt.nearest(&v, allowed, pool_n)));
    }

    // Reciprocal Rank Fusion ponderada
    let mut score: HashMap<i64, f64> = HashMap::new();
    for (weight, list) in lists {
        for (rank, id) in list.into_iter().enumerate() {
            *score.entry(id).or_default() += weight / (60.0 + rank as f64 + 1.0);
        }
    }
    let mut ranked: Vec<(i64, f64)> = score.into_iter().collect();
    ranked.sort_unstable_by(|a, b| b.1.total_cmp(&a.1));
    ranked.truncate(top);
    Ok(ranked)
}

pub fn run(conn: &Connection, query: Option<String>, image: Option<String>, top: usize) -> Result<()> {
    let mut models = Models::load()?;
    let index = VectorIndex::load(conn)?;
    let bytes = match &image {
        Some(path) => Some(std::fs::read(path)?),
        None => None,
    };
    let ranked = rank(&index, &mut models, query.as_deref(), bytes.as_deref(), None, top)?;
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
