//! Previews ausentes: exporta a lista para o gerador dentro do Revit e importa os PNGs gerados.

use anyhow::{Context, Result};
use rusqlite::{params, Connection, OptionalExtension};
use serde_json::json;
use std::path::Path;

/// Escreve `missing.json` com as familias sem preview, para o script gerador de previews no Revit.
pub fn export_missing(conn: &Connection, out: &Path) -> Result<()> {
    let mut st = conn.prepare("SELECT sha256, path FROM families WHERE has_preview = 0 ORDER BY id")?;
    let list: Vec<_> = st
        .query_map([], |r| Ok(json!({ "sha": r.get::<_, String>(0)?, "path": r.get::<_, String>(1)? })))?
        .collect::<std::result::Result<_, _>>()?;
    std::fs::write(out, serde_json::to_string_pretty(&list)?)?;
    println!("{} familias sem preview -> {}", list.len(), out.display());
    Ok(())
}

/// Importa `<sha256>.png` de uma pasta (gerados pelo script do Revit).
pub fn import(conn: &Connection, dir: &Path) -> Result<()> {
    let (mut ok, mut skip) = (0u32, 0u32);
    for entry in std::fs::read_dir(dir).with_context(|| format!("pasta {}", dir.display()))? {
        let p = entry?.path();
        if p.extension().and_then(|e| e.to_str()).map(|e| e.eq_ignore_ascii_case("png")) != Some(true) {
            continue;
        }
        let sha = p.file_stem().unwrap().to_string_lossy().to_string();
        let bytes = std::fs::read(&p)?;
        let Ok(img) = image::load_from_memory(&bytes) else { skip += 1; continue };
        let id: Option<i64> = conn
            .query_row("SELECT id FROM families WHERE sha256 = ?1", [&sha], |r| r.get(0))
            .optional()?;
        let Some(id) = id else { skip += 1; continue };
        conn.execute(
            "INSERT INTO previews (family_id, png, width, height, source) VALUES (?1,?2,?3,?4,'generated')
             ON CONFLICT (family_id) DO UPDATE SET png = excluded.png, width = excluded.width,
                height = excluded.height, source = 'generated'",
            params![id, bytes, img.width() as i64, img.height() as i64],
        )?;
        conn.execute("UPDATE families SET has_preview = 1 WHERE id = ?1", [id])?;
        // Novo preview: invalida o embedding para ser recalculado.
        conn.execute("DELETE FROM embeddings WHERE family_id = ?1", [id])?;
        ok += 1;
    }
    println!("previews importados: {ok} | ignorados: {skip}");
    if ok > 0 {
        println!("rode `gbfam enrich` (para os que ainda nao tem descricao) e `gbfam embed` em seguida");
    }
    Ok(())
}
