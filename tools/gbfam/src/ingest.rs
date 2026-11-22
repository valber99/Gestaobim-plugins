//! Varre a pasta de familias, extrai dados do .rfa e grava no SQLite (retomavel).

use crate::extract::{self, Extracted};
use anyhow::Result;
use futures::StreamExt;
use indicatif::{ProgressBar, ProgressStyle};
use rusqlite::{params, Connection, OptionalExtension};
use sha2::{Digest, Sha256};
use std::collections::HashSet;
use std::path::{Path, PathBuf};
use walkdir::WalkDir;

struct Job {
    path: PathBuf,
    rel: String,
    name: String,
    size: i64,
    sha: String,
    folder_category: Option<String>,
    folder_hint: Option<String>,
    extracted: Result<Extracted, String>,
}

fn known_paths(conn: &Connection) -> Result<HashSet<String>> {
    let mut set = HashSet::new();
    let mut st = conn.prepare("SELECT path, duplicates FROM families")?;
    let rows = st.query_map([], |r| Ok((r.get::<_, String>(0)?, r.get::<_, String>(1)?)))?;
    for row in rows {
        let (path, dups) = row?;
        set.insert(path);
        if let Ok(v) = serde_json::from_str::<Vec<String>>(&dups) {
            set.extend(v);
        }
    }
    Ok(set)
}

pub async fn run(conn: &Connection, root: &Path, workers: usize) -> Result<()> {
    let known = known_paths(conn)?;
    let files: Vec<PathBuf> = WalkDir::new(root)
        .into_iter()
        .filter_map(|e| e.ok())
        .filter(|e| e.file_type().is_file())
        .map(|e| e.into_path())
        .filter(|p| is_family(p))
        .filter(|p| !known.contains(&p.to_string_lossy().to_string()))
        .collect();

    println!("{} arquivos .rfa novos (ja conhecidos: {})", files.len(), known.len());
    let pb = ProgressBar::new(files.len() as u64);
    pb.set_style(ProgressStyle::with_template("{bar:40} {pos}/{len}").unwrap());

    let root = root.to_path_buf();
    let mut stream = futures::stream::iter(files)
        .map(|p| {
            let root = root.clone();
            tokio::task::spawn_blocking(move || build_job(&root, p))
        })
        .buffer_unordered(workers.max(1));

    let (mut inserted, mut dups, mut sem_preview, mut erros) = (0u32, 0u32, 0u32, 0u32);
    conn.execute_batch("BEGIN")?;
    let mut pending = 0;
    while let Some(res) = stream.next().await {
        pb.inc(1);
        let Ok(Some(job)) = res else { erros += 1; continue };
        let ex = match &job.extracted {
            Ok(e) => e,
            Err(msg) => {
                erros += 1;
                pb.println(format!("erro: {} -> {msg}", job.rel));
                continue;
            }
        };
        let path = job.path.to_string_lossy().to_string();

        let existing: Option<(i64, String, String)> = conn
            .query_row(
                "SELECT id, path, duplicates FROM families WHERE sha256 = ?1",
                [&job.sha],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?)),
            )
            .optional()?;

        if let Some((id, first_path, dups_json)) = existing {
            let mut list: Vec<String> = serde_json::from_str(&dups_json).unwrap_or_default();
            if first_path != path && !list.contains(&path) {
                list.push(path);
                conn.execute(
                    "UPDATE families SET duplicates = ?1 WHERE id = ?2",
                    params![serde_json::to_string(&list)?, id],
                )?;
            }
            dups += 1;
        } else {
            conn.execute(
                "INSERT INTO families (sha256, path, rel_path, name, size_bytes, folder_category, folder_hint,
                                       revit_version, revit_build, revit_category, basic_info, part_atom, has_preview)
                 VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13)",
                params![
                    job.sha,
                    path,
                    job.rel,
                    job.name,
                    job.size,
                    job.folder_category,
                    job.folder_hint,
                    ex.revit_version,
                    ex.revit_build,
                    ex.revit_category,
                    ex.basic_info.to_string(),
                    ex.part_atom.to_string(),
                    ex.png.is_some() as i64
                ],
            )?;
            let id = conn.last_insert_rowid();
            inserted += 1;
            if let Some(png) = &ex.png {
                let (w, h) = png_size(png);
                conn.execute(
                    "INSERT INTO previews (family_id, png, width, height, source) VALUES (?1,?2,?3,?4,'embedded')",
                    params![id, png, w, h],
                )?;
            } else {
                sem_preview += 1;
            }
        }
        pending += 1;
        if pending >= 200 {
            conn.execute_batch("COMMIT; BEGIN")?;
            pending = 0;
        }
    }
    conn.execute_batch("COMMIT")?;
    pb.finish_and_clear();
    println!("inseridas: {inserted} | duplicadas (mesmo conteudo): {dups} | sem preview: {sem_preview} | com erro: {erros}");
    Ok(())
}

/// `.rfa` valido, ignorando backups do Revit (`nome.0001.rfa`).
pub fn is_family(p: &Path) -> bool {
    let name = p.file_name().and_then(|n| n.to_str()).unwrap_or("").to_lowercase();
    if !name.ends_with(".rfa") {
        return false;
    }
    let stem = &name[..name.len() - 4];
    !(stem.len() > 5
        && stem.as_bytes()[stem.len() - 5] == b'.'
        && stem[stem.len() - 4..].bytes().all(|b| b.is_ascii_digit()))
}

fn build_job(root: &Path, path: PathBuf) -> Option<Job> {
    let bytes = std::fs::read(&path);
    let rel = path.strip_prefix(root).unwrap_or(&path).to_string_lossy().to_string();
    let name = path.file_stem()?.to_string_lossy().to_string();
    let comps: Vec<String> = Path::new(&rel)
        .parent()
        .map(|p| p.components().map(|c| c.as_os_str().to_string_lossy().to_string()).collect())
        .unwrap_or_default();
    let folder_category = comps.first().map(|c| c.trim_start_matches('#').to_string());
    let folder_hint = if comps.len() > 1 { Some(comps[1..].join(" / ")) } else { None };

    let (sha, size, extracted) = match bytes {
        Ok(b) => {
            let sha = hex::encode(Sha256::digest(&b));
            (sha, b.len() as i64, extract::extract(&path).map_err(|e| e.to_string()))
        }
        Err(e) => (String::new(), 0, Err(e.to_string())),
    };
    Some(Job { path, rel, name, size, sha, folder_category, folder_hint, extracted })
}

fn png_size(png: &[u8]) -> (i64, i64) {
    if png.len() >= 24 {
        let w = u32::from_be_bytes([png[16], png[17], png[18], png[19]]) as i64;
        let h = u32::from_be_bytes([png[20], png[21], png[22], png[23]]) as i64;
        (w, h)
    } else {
        (0, 0)
    }
}
