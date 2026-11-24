//! Monta o pacote da biblioteca para distribuir: um banco SQLite compacto e so as familias unicas (sem as
//! duplicatas), na mesma estrutura de pastas (`rel_path`) que o plugin do Revit espera.

use anyhow::{bail, Context, Result};
use rusqlite::Connection;
use std::path::{Path, PathBuf};

pub fn run(conn: &Connection, root: &Path, out: &Path, dry_run: bool) -> Result<()> {
    if !root.is_dir() {
        bail!("pasta das familias nao encontrada: {}", root.display());
    }
    let mut st = conn.prepare("SELECT rel_path, size_bytes FROM families ORDER BY id")?;
    let rows: Vec<(String, i64)> = st
        .query_map([], |r| Ok((r.get(0)?, r.get(1)?)))?
        .collect::<std::result::Result<_, _>>()?;

    let mut missing = Vec::new();
    let mut total: u64 = 0;
    let mut to_copy: Vec<(PathBuf, PathBuf)> = Vec::new();
    for (rel, size) in &rows {
        let rel_clean = rel.replace('\\', "/");
        // Mesma regra do plugin: nada de caminho absoluto nem de "..".
        if rel_clean.starts_with('/') || rel_clean.split('/').any(|c| c == "..") || rel_clean.contains(':') {
            bail!("caminho invalido no banco: {rel}");
        }
        let from = rel_clean.split('/').fold(root.to_path_buf(), |p, c| p.join(c));
        let to = rel_clean.split('/').fold(out.join("familias"), |p, c| p.join(c));
        if from.is_file() {
            total += *size as u64;
            to_copy.push((from, to));
        } else {
            missing.push(rel.clone());
        }
    }

    println!(
        "{} familias no banco | {} arquivos encontrados ({:.1} MB) | {} ausentes",
        rows.len(),
        to_copy.len(),
        total as f64 / 1_048_576.0,
        missing.len()
    );
    for m in missing.iter().take(10) {
        println!("  ausente: {m}");
    }
    if dry_run {
        println!("(simulacao: nada foi copiado)");
        return Ok(());
    }

    std::fs::create_dir_all(out)?;
    let db_out = out.join("gbfam.sqlite");
    if db_out.exists() {
        bail!("{} ja existe; escolha uma pasta de saida vazia", db_out.display());
    }
    // VACUUM INTO gera uma copia consistente e compacta, sem o arquivo -wal.
    conn.execute("VACUUM INTO ?1", [db_out.to_string_lossy().as_ref()])
        .context("falha ao copiar o banco")?;

    for (i, (from, to)) in to_copy.iter().enumerate() {
        if let Some(dir) = to.parent() {
            std::fs::create_dir_all(dir)?;
        }
        std::fs::copy(from, to).with_context(|| format!("copiando {}", from.display()))?;
        if (i + 1) % 500 == 0 {
            println!("  {}/{}", i + 1, to_copy.len());
        }
    }
    println!("pacote da biblioteca pronto em {}", out.display());
    Ok(())
}
