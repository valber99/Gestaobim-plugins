use anyhow::{Context, Result};
use rusqlite::{Connection, OpenFlags};
use std::path::Path;

/// Dimensoes dos vetores. CLIP ViT-B/32 = 512, multilingual-e5-small = 384.
pub const DIM_CLIP: usize = 512;
pub const DIM_E5: usize = 384;

pub const SCHEMA: &str = include_str!("schema.sql");

pub fn open(path: &Path) -> Result<Connection> {
    let conn = Connection::open(path).with_context(|| format!("nao abriu {}", path.display()))?;
    conn.execute_batch("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;")?;
    Ok(conn)
}

pub fn open_readonly(path: &Path) -> Result<Connection> {
    let conn = Connection::open_with_flags(path, OpenFlags::SQLITE_OPEN_READ_ONLY | OpenFlags::SQLITE_OPEN_NO_MUTEX)
        .with_context(|| format!("nao abriu {} (somente leitura)", path.display()))?;
    conn.execute_batch("PRAGMA busy_timeout=5000;")?;
    Ok(conn)
}

pub fn init(conn: &Connection) -> Result<()> {
    conn.execute_batch(SCHEMA).context("falha ao criar o schema")
}

/// f32 little-endian, 4 bytes por dimensao.
pub fn vec_to_blob(v: &[f32]) -> Vec<u8> {
    v.iter().flat_map(|x| x.to_le_bytes()).collect()
}

#[allow(dead_code)]
pub fn blob_to_vec(b: &[u8]) -> Vec<f32> {
    b.chunks_exact(4).map(|c| f32::from_le_bytes([c[0], c[1], c[2], c[3]])).collect()
}
