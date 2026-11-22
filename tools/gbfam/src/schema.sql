CREATE TABLE IF NOT EXISTS families (
    id              INTEGER PRIMARY KEY AUTOINCREMENT,
    sha256          TEXT NOT NULL UNIQUE,
    path            TEXT NOT NULL,
    rel_path        TEXT NOT NULL,
    name            TEXT NOT NULL,
    size_bytes      INTEGER NOT NULL,
    folder_category TEXT,
    folder_hint     TEXT,
    revit_version   TEXT,
    revit_build     TEXT,
    revit_category  TEXT,
    basic_info      TEXT,                       -- JSON
    part_atom       TEXT,                       -- JSON
    duplicates      TEXT NOT NULL DEFAULT '[]', -- JSON: outros caminhos com o mesmo conteudo
    has_preview     INTEGER NOT NULL DEFAULT 0,
    created_at      TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE IF NOT EXISTS previews (
    family_id INTEGER PRIMARY KEY REFERENCES families(id) ON DELETE CASCADE,
    png       BLOB NOT NULL,
    width     INTEGER,
    height    INTEGER,
    source    TEXT NOT NULL                     -- 'embedded' | 'generated'
);

CREATE TABLE IF NOT EXISTS enrichments (
    family_id  INTEGER PRIMARY KEY REFERENCES families(id) ON DELETE CASCADE,
    model      TEXT NOT NULL,
    data       TEXT NOT NULL,                   -- JSON completo devolvido pela IA
    descricao  TEXT,
    categoria  TEXT,
    tags       TEXT,                            -- JSON array
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Vetores normalizados, f32 little-endian. CLIP = 512 dims, e5 = 384 dims.
CREATE TABLE IF NOT EXISTS embeddings (
    family_id  INTEGER PRIMARY KEY REFERENCES families(id) ON DELETE CASCADE,
    img        BLOB,   -- CLIP imagem
    clip_txt   BLOB,   -- CLIP texto (nome + categoria + tags)
    desc_vec   BLOB,   -- e5-small sobre a descricao gerada
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS families_category_idx ON families (folder_category);
CREATE INDEX IF NOT EXISTS enrichments_categoria_idx ON enrichments (categoria);
