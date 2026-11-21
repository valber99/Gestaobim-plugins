//! Previews ausentes: exporta a lista para o gerador dentro do Revit e importa os PNGs gerados.

use anyhow::{Context, Result};
use serde_json::json;
use sqlx::{PgPool, Row};
use std::path::Path;

/// Escreve `missing.json` com as familias sem preview, para `tools/revit_gerar_previews.py`.
pub async fn export_missing(pool: &PgPool, out: &Path) -> Result<()> {
    let rows = sqlx::query("SELECT sha256, path FROM families WHERE NOT has_preview ORDER BY id")
        .fetch_all(pool)
        .await?;
    let list: Vec<_> = rows
        .iter()
        .map(|r| json!({ "sha": r.get::<String, _>("sha256"), "path": r.get::<String, _>("path") }))
        .collect();
    std::fs::write(out, serde_json::to_string_pretty(&list)?)?;
    println!("{} familias sem preview -> {}", list.len(), out.display());
    Ok(())
}

/// Importa `<sha256>.png` de uma pasta (gerados pelo script do Revit).
pub async fn import(pool: &PgPool, dir: &Path) -> Result<()> {
    let (mut ok, mut skip) = (0u32, 0u32);
    for entry in std::fs::read_dir(dir).with_context(|| format!("pasta {}", dir.display()))? {
        let p = entry?.path();
        if p.extension().and_then(|e| e.to_str()).map(|e| e.eq_ignore_ascii_case("png")) != Some(true) {
            continue;
        }
        let sha = p.file_stem().unwrap().to_string_lossy().to_string();
        let bytes = std::fs::read(&p)?;
        let Ok(img) = image::load_from_memory(&bytes) else { skip += 1; continue };
        let Some(id) = sqlx::query("SELECT id FROM families WHERE sha256 = $1")
            .bind(&sha)
            .fetch_optional(pool)
            .await?
            .map(|r| r.get::<i64, _>(0))
        else {
            skip += 1;
            continue;
        };
        sqlx::query(
            "INSERT INTO previews (family_id, png, width, height, source) VALUES ($1,$2,$3,$4,'generated')
             ON CONFLICT (family_id) DO UPDATE SET png = EXCLUDED.png, width = EXCLUDED.width,
                height = EXCLUDED.height, source = 'generated'",
        )
        .bind(id)
        .bind(&bytes)
        .bind(img.width() as i32)
        .bind(img.height() as i32)
        .execute(pool)
        .await?;
        // Novo preview: invalida o embedding de imagem para ser recalculado.
        sqlx::query("UPDATE families SET has_preview = true WHERE id = $1").bind(id).execute(pool).await?;
        sqlx::query("DELETE FROM embeddings WHERE family_id = $1").bind(id).execute(pool).await?;
        ok += 1;
    }
    println!("previews importados: {ok} | ignorados: {skip}");
    if ok > 0 {
        println!("rode `gbfam enrich` (para os que ainda nao tem descricao) e `gbfam embed` em seguida");
    }
    Ok(())
}
