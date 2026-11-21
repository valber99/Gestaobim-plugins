use anyhow::{Context, Result};
use sqlx::postgres::PgPoolOptions;
use sqlx::PgPool;

/// Dimensoes dos vetores. CLIP ViT-B/32 = 512, multilingual-e5-small = 384.
pub const DIM_CLIP: usize = 512;
pub const DIM_E5: usize = 384;

const SCHEMA: &str = r#"
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS families (
    id              BIGSERIAL PRIMARY KEY,
    sha256          TEXT NOT NULL UNIQUE,
    path            TEXT NOT NULL,
    rel_path        TEXT NOT NULL,
    name            TEXT NOT NULL,
    size_bytes      BIGINT NOT NULL,
    folder_category TEXT,
    folder_hint     TEXT,
    revit_version   TEXT,
    revit_build     TEXT,
    revit_category  TEXT,
    basic_info      JSONB,
    part_atom       JSONB,
    duplicates      TEXT[] NOT NULL DEFAULT '{}',
    has_preview     BOOLEAN NOT NULL DEFAULT false,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS previews (
    family_id BIGINT PRIMARY KEY REFERENCES families(id) ON DELETE CASCADE,
    png       BYTEA NOT NULL,
    width     INT,
    height    INT,
    source    TEXT NOT NULL  -- 'embedded' | 'generated'
);

CREATE TABLE IF NOT EXISTS enrichments (
    family_id  BIGINT PRIMARY KEY REFERENCES families(id) ON DELETE CASCADE,
    model      TEXT NOT NULL,
    data       JSONB NOT NULL,
    descricao  TEXT,
    categoria  TEXT,
    tags       TEXT[],
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS embeddings (
    family_id  BIGINT PRIMARY KEY REFERENCES families(id) ON DELETE CASCADE,
    img        vector(512),   -- CLIP imagem
    clip_txt   vector(512),   -- CLIP texto (nome + categoria + tags)
    desc_vec   vector(384),   -- e5-small sobre a descricao gerada
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS embeddings_img_hnsw  ON embeddings USING hnsw (img vector_cosine_ops);
CREATE INDEX IF NOT EXISTS embeddings_clip_txt_hnsw ON embeddings USING hnsw (clip_txt vector_cosine_ops);
CREATE INDEX IF NOT EXISTS embeddings_desc_hnsw ON embeddings USING hnsw (desc_vec vector_cosine_ops);
CREATE INDEX IF NOT EXISTS families_category_idx ON families (folder_category);
CREATE INDEX IF NOT EXISTS enrichments_tags_idx ON enrichments USING gin (tags);
"#;

pub async fn connect(url: &str) -> Result<PgPool> {
    PgPoolOptions::new()
        .max_connections(8)
        .connect(url)
        .await
        .context("falha ao conectar no Postgres (confira DATABASE_URL, usuario e senha)")
}

pub async fn init(pool: &PgPool) -> Result<()> {
    // raw_sql permite varios comandos numa string.
    sqlx::raw_sql(SCHEMA)
        .execute(pool)
        .await
        .context("falha ao criar o schema (a extensao pgvector esta instalada neste banco?)")?;
    Ok(())
}

/// Formata um vetor no literal do pgvector: `[0.1,0.2,...]`.
pub fn vec_literal(v: &[f32]) -> String {
    let mut s = String::with_capacity(v.len() * 10 + 2);
    s.push('[');
    for (i, x) in v.iter().enumerate() {
        if i > 0 {
            s.push(',');
        }
        s.push_str(&x.to_string());
    }
    s.push(']');
    s
}
