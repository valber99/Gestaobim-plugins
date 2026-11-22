//! Catalogo versionado para distribuir pelo servidor: snapshot do banco, deltas entre versoes, tabela
//! `sha256 -> caminho no Drive` e um manifesto assinado com Ed25519.
//!
//! - O snapshot (`catalog-<v>.sqlite.gz`) leva tudo que a busca precisa (metadados, previews, vetores) e nenhum `.rfa`.
//! - O delta (`delta-<a>-<b>.json.gz`) e DADO, nunca SQL: o plugin aplica as linhas com comandos parametrizados.
//! - O manifesto e assinado; o plugin leva a chave publica e recusa manifesto adulterado ou de versao menor.

use anyhow::{bail, Context, Result};
use base64::{engine::general_purpose::STANDARD as B64, Engine};
use ed25519_dalek::{Signature, Signer, SigningKey, Verifier, VerifyingKey};
use flate2::{read::GzDecoder, write::GzEncoder, Compression};
use rusqlite::{Connection, OptionalExtension};
#[cfg(test)]
use rusqlite::params;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::collections::{BTreeMap, BTreeSet};
use std::io::{Read, Write};
use std::path::Path;

pub const MIN_PLUGIN: &str = "0.2.0";

// ---------------------------------------------------------------- tipos

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct FileRef {
    pub file: String,
    pub sha256: String,
    pub bytes: u64,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct DeltaRef {
    pub from: u32,
    pub to: u32,
    pub file: String,
    pub sha256: String,
    pub bytes: u64,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct Manifest {
    pub schema: u32,
    pub version: u32,
    pub generated_at: String,
    pub families: u32,
    pub min_plugin: String,
    pub snapshot: FileRef,
    pub paths: FileRef,
    pub deltas: Vec<DeltaRef>,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct FamilyRow {
    pub sha256: String,
    pub path: String,
    pub rel_path: String,
    pub name: String,
    pub size_bytes: i64,
    pub folder_category: Option<String>,
    pub folder_hint: Option<String>,
    pub revit_version: Option<String>,
    pub revit_build: Option<String>,
    pub revit_category: Option<String>,
    pub basic_info: Option<String>,
    pub part_atom: Option<String>,
    pub duplicates: String,
    pub has_preview: i64,
    pub created_at: String,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct PreviewRow {
    pub png_b64: String,
    pub width: Option<i64>,
    pub height: Option<i64>,
    pub source: String,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct EnrichmentRow {
    pub model: String,
    pub data: String,
    pub descricao: Option<String>,
    pub categoria: Option<String>,
    pub tags: Option<String>,
    pub created_at: String,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct EmbeddingRow {
    pub img_b64: Option<String>,
    pub clip_txt_b64: Option<String>,
    pub desc_vec_b64: Option<String>,
    pub created_at: String,
}

#[derive(Serialize, Deserialize, Clone, Debug, PartialEq)]
pub struct Bundle {
    pub family: FamilyRow,
    pub preview: Option<PreviewRow>,
    pub enrichment: Option<EnrichmentRow>,
    pub embedding: Option<EmbeddingRow>,
}

#[derive(Serialize, Deserialize, Debug)]
pub struct Delta {
    pub from: u32,
    pub to: u32,
    pub families_after: u32,
    pub removed: Vec<String>,
    pub upserts: Vec<Bundle>,
}

// ---------------------------------------------------------------- utilitarios

fn sha_hex(data: &[u8]) -> String {
    hex::encode(Sha256::digest(data))
}

fn gzip(data: &[u8]) -> Result<Vec<u8>> {
    let mut e = GzEncoder::new(Vec::new(), Compression::new(6));
    e.write_all(data)?;
    Ok(e.finish()?)
}

pub fn gunzip(data: &[u8]) -> Result<Vec<u8>> {
    let mut out = Vec::new();
    GzDecoder::new(data).read_to_end(&mut out)?;
    Ok(out)
}

fn now_iso() -> String {
    let secs = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0);
    // dias desde 1970 -> data civil (algoritmo de Howard Hinnant)
    let days = secs.div_euclid(86_400);
    let rem = secs.rem_euclid(86_400);
    let z = days + 719_468;
    let era = z.div_euclid(146_097);
    let doe = z.rem_euclid(146_097);
    let yoe = (doe - doe / 1460 + doe / 36_524 - doe / 146_096) / 365;
    let y = yoe + era * 400;
    let doy = doe - (365 * yoe + yoe / 4 - yoe / 100);
    let mp = (5 * doy + 2) / 153;
    let d = doy - (153 * mp + 2) / 5 + 1;
    let m = if mp < 10 { mp + 3 } else { mp - 9 };
    let y = if m <= 2 { y + 1 } else { y };
    format!("{y:04}-{m:02}-{d:02}T{:02}:{:02}:{:02}Z", rem / 3600, (rem % 3600) / 60, rem % 60)
}

fn opt_b64(b: Option<Vec<u8>>) -> Option<String> {
    b.map(|v| B64.encode(v))
}

#[cfg(test)]
fn from_b64(s: &Option<String>) -> Result<Option<Vec<u8>>> {
    match s {
        Some(x) => Ok(Some(B64.decode(x).context("base64 invalido")?)),
        None => Ok(None),
    }
}

// ---------------------------------------------------------------- leitura do banco

/// Todas as linhas de uma familia, indexadas pelo sha256 (a chave estavel entre versoes).
pub fn load_bundles(conn: &Connection) -> Result<BTreeMap<String, Bundle>> {
    let mut out = BTreeMap::new();
    let mut st = conn.prepare(
        "SELECT f.id, f.sha256, f.path, f.rel_path, f.name, f.size_bytes, f.folder_category, f.folder_hint,
                f.revit_version, f.revit_build, f.revit_category, f.basic_info, f.part_atom, f.duplicates,
                f.has_preview, f.created_at FROM families f ORDER BY f.sha256",
    )?;
    let rows: Vec<(i64, FamilyRow)> = st
        .query_map([], |r| {
            Ok((
                r.get::<_, i64>(0)?,
                FamilyRow {
                    sha256: r.get(1)?,
                    path: r.get(2)?,
                    rel_path: r.get(3)?,
                    name: r.get(4)?,
                    size_bytes: r.get(5)?,
                    folder_category: r.get(6)?,
                    folder_hint: r.get(7)?,
                    revit_version: r.get(8)?,
                    revit_build: r.get(9)?,
                    revit_category: r.get(10)?,
                    basic_info: r.get(11)?,
                    part_atom: r.get(12)?,
                    duplicates: r.get(13)?,
                    has_preview: r.get(14)?,
                    created_at: r.get(15)?,
                },
            ))
        })?
        .collect::<std::result::Result<_, _>>()?;

    let mut pv = conn.prepare("SELECT png, width, height, source FROM previews WHERE family_id = ?1")?;
    let mut en = conn.prepare("SELECT model, data, descricao, categoria, tags, created_at FROM enrichments WHERE family_id = ?1")?;
    let mut em = conn.prepare("SELECT img, clip_txt, desc_vec, created_at FROM embeddings WHERE family_id = ?1")?;
    for (id, family) in rows {
        let preview = pv
            .query_row([id], |r| {
                Ok(PreviewRow { png_b64: B64.encode(r.get::<_, Vec<u8>>(0)?), width: r.get(1)?, height: r.get(2)?, source: r.get(3)? })
            })
            .optional()?;
        let enrichment = en
            .query_row([id], |r| {
                Ok(EnrichmentRow { model: r.get(0)?, data: r.get(1)?, descricao: r.get(2)?, categoria: r.get(3)?, tags: r.get(4)?, created_at: r.get(5)? })
            })
            .optional()?;
        let embedding = em
            .query_row([id], |r| {
                Ok(EmbeddingRow {
                    img_b64: opt_b64(r.get::<_, Option<Vec<u8>>>(0)?),
                    clip_txt_b64: opt_b64(r.get::<_, Option<Vec<u8>>>(1)?),
                    desc_vec_b64: opt_b64(r.get::<_, Option<Vec<u8>>>(2)?),
                    created_at: r.get(3)?,
                })
            })
            .optional()?;
        out.insert(family.sha256.clone(), Bundle { family, preview, enrichment, embedding });
    }
    Ok(out)
}

/// Tira do registro os caminhos absolutos do computador do administrador: `path` vira o caminho relativo e as duplicatas
/// ficam relativas a mesma raiz (o plugin so usa a quantidade delas). Idempotente.
pub fn sanitize(b: &mut Bundle) {
    let f = &mut b.family;
    let norm = |s: &str| s.replace('/', "\\");
    let path = norm(&f.path);
    let rel = norm(&f.rel_path);
    let root = path
        .to_lowercase()
        .ends_with(&rel.to_lowercase())
        .then(|| path[..path.len() - rel.len()].to_string())
        .unwrap_or_default();
    let relative = |p: &str| -> String {
        let p = norm(p);
        if !root.is_empty() && p.to_lowercase().starts_with(&root.to_lowercase()) {
            p[root.len()..].to_string()
        } else if p.contains(':') || p.starts_with('\\') {
            p.rsplit('\\').next().unwrap_or("").to_string()
        } else {
            p
        }
    };
    if let Ok(list) = serde_json::from_str::<Vec<String>>(&f.duplicates) {
        f.duplicates = serde_json::to_string(&list.iter().map(|d| relative(d)).collect::<Vec<_>>()).unwrap_or_else(|_| "[]".into());
    } else {
        f.duplicates = "[]".into();
    }
    f.path = f.rel_path.clone();
}

/// Impressao digital do conteudo de uma familia: muda quando qualquer dado dela muda.
pub fn fingerprint(b: &Bundle) -> String {
    let mut h = Sha256::new();
    let j = serde_json::to_vec(b).expect("serializar");
    h.update(&j);
    hex::encode(h.finalize())
}

// ---------------------------------------------------------------- escrita (aplicar delta)

/// Implementacao de referencia (usada nos testes): o plugin C# faz o mesmo e a compatibilidade e testada nos dois lados.
#[cfg(test)]
pub fn apply_delta(conn: &mut Connection, d: &Delta) -> Result<()> {
    let tx = conn.transaction()?;
    tx.execute_batch("PRAGMA defer_foreign_keys = ON;")?;
    for sha in &d.removed {
        tx.execute("DELETE FROM families WHERE sha256 = ?1", [sha])?;
    }
    for b in &d.upserts {
        let f = &b.family;
        tx.execute(
            "INSERT INTO families (sha256, path, rel_path, name, size_bytes, folder_category, folder_hint,
                 revit_version, revit_build, revit_category, basic_info, part_atom, duplicates, has_preview, created_at)
             VALUES (?1,?2,?3,?4,?5,?6,?7,?8,?9,?10,?11,?12,?13,?14,?15)
             ON CONFLICT(sha256) DO UPDATE SET path=excluded.path, rel_path=excluded.rel_path, name=excluded.name,
                 size_bytes=excluded.size_bytes, folder_category=excluded.folder_category, folder_hint=excluded.folder_hint,
                 revit_version=excluded.revit_version, revit_build=excluded.revit_build, revit_category=excluded.revit_category,
                 basic_info=excluded.basic_info, part_atom=excluded.part_atom, duplicates=excluded.duplicates,
                 has_preview=excluded.has_preview, created_at=excluded.created_at",
            params![f.sha256, f.path, f.rel_path, f.name, f.size_bytes, f.folder_category, f.folder_hint, f.revit_version,
                    f.revit_build, f.revit_category, f.basic_info, f.part_atom, f.duplicates, f.has_preview, f.created_at],
        )?;
        let id: i64 = tx.query_row("SELECT id FROM families WHERE sha256 = ?1", [&f.sha256], |r| r.get(0))?;
        tx.execute("DELETE FROM previews WHERE family_id = ?1", [id])?;
        tx.execute("DELETE FROM enrichments WHERE family_id = ?1", [id])?;
        tx.execute("DELETE FROM embeddings WHERE family_id = ?1", [id])?;
        if let Some(p) = &b.preview {
            tx.execute(
                "INSERT INTO previews (family_id, png, width, height, source) VALUES (?1,?2,?3,?4,?5)",
                params![id, B64.decode(&p.png_b64)?, p.width, p.height, p.source],
            )?;
        }
        if let Some(e) = &b.enrichment {
            tx.execute(
                "INSERT INTO enrichments (family_id, model, data, descricao, categoria, tags, created_at) VALUES (?1,?2,?3,?4,?5,?6,?7)",
                params![id, e.model, e.data, e.descricao, e.categoria, e.tags, e.created_at],
            )?;
        }
        if let Some(m) = &b.embedding {
            tx.execute(
                "INSERT INTO embeddings (family_id, img, clip_txt, desc_vec, created_at) VALUES (?1,?2,?3,?4,?5)",
                params![id, from_b64(&m.img_b64)?, from_b64(&m.clip_txt_b64)?, from_b64(&m.desc_vec_b64)?, m.created_at],
            )?;
        }
    }
    let count: i64 = tx.query_row("SELECT count(*) FROM families", [], |r| r.get(0))?;
    if count as u32 != d.families_after {
        bail!("delta inconsistente: {} familias depois de aplicar, esperado {}", count, d.families_after);
    }
    tx.execute("CREATE TABLE IF NOT EXISTS catalog_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL)", [])?;
    tx.execute("INSERT INTO catalog_meta (key, value) VALUES ('version', ?1) ON CONFLICT(key) DO UPDATE SET value = excluded.value", [d.to.to_string()])?;
    tx.commit()?;
    Ok(())
}

pub fn make_delta(from: u32, to: u32, old: &BTreeMap<String, Bundle>, new: &BTreeMap<String, Bundle>) -> Delta {
    let removed: Vec<String> = old.keys().filter(|k| !new.contains_key(*k)).cloned().collect();
    let upserts: Vec<Bundle> = new
        .iter()
        .filter(|(sha, b)| match old.get(*sha) {
            None => true,
            Some(o) => fingerprint(o) != fingerprint(b),
        })
        .map(|(_, b)| b.clone())
        .collect();
    Delta { from, to, families_after: new.len() as u32, removed, upserts }
}

// ---------------------------------------------------------------- chaves e assinatura

pub fn gen_key(out: &Path) -> Result<String> {
    if out.exists() {
        bail!("{} ja existe; nao vou sobrescrever a chave de assinatura", out.display());
    }
    let mut seed = [0u8; 32];
    getrandom::fill(&mut seed).map_err(|e| anyhow::anyhow!("sem aleatoriedade do sistema: {e}"))?;
    let sk = SigningKey::from_bytes(&seed);
    std::fs::write(out, hex::encode(seed))?;
    Ok(B64.encode(sk.verifying_key().to_bytes()))
}

fn load_signing_key(path: &Path) -> Result<SigningKey> {
    let s = std::fs::read_to_string(path).with_context(|| format!("nao li a chave {}", path.display()))?;
    let bytes = hex::decode(s.trim()).context("chave de assinatura invalida")?;
    let seed: [u8; 32] = bytes.try_into().map_err(|_| anyhow::anyhow!("a chave deve ter 32 bytes"))?;
    Ok(SigningKey::from_bytes(&seed))
}

pub fn sign_manifest(manifest_bytes: &[u8], key: &SigningKey) -> String {
    B64.encode(key.sign(manifest_bytes).to_bytes())
}

pub fn verify_manifest(manifest_bytes: &[u8], sig_b64: &str, pubkey_b64: &str) -> Result<()> {
    let pk: [u8; 32] = B64.decode(pubkey_b64.trim())?.try_into().map_err(|_| anyhow::anyhow!("chave publica invalida"))?;
    let vk = VerifyingKey::from_bytes(&pk).map_err(|e| anyhow::anyhow!("chave publica invalida: {e}"))?;
    let sig: [u8; 64] = B64.decode(sig_b64.trim())?.try_into().map_err(|_| anyhow::anyhow!("assinatura invalida"))?;
    vk.verify(manifest_bytes, &Signature::from_bytes(&sig))
        .map_err(|_| anyhow::anyhow!("assinatura do manifesto NAO confere"))
}

// ---------------------------------------------------------------- exportacao

fn write_file(dir: &Path, name: &str, data: &[u8]) -> Result<FileRef> {
    std::fs::write(dir.join(name), data)?;
    Ok(FileRef { file: name.to_string(), sha256: sha_hex(data), bytes: data.len() as u64 })
}

/// Gera a proxima versao do catalogo em `out`. Devolve a versao criada.
pub fn export(conn: &Connection, out: &Path, key_file: &Path, version: Option<u32>) -> Result<u32> {
    std::fs::create_dir_all(out)?;
    let key = load_signing_key(key_file)?;
    let manifest_path = out.join("manifest.json");
    let prev: Option<Manifest> = if manifest_path.is_file() {
        Some(serde_json::from_slice(&std::fs::read(&manifest_path)?).context("manifest.json anterior ilegivel")?)
    } else {
        None
    };
    let next = version.unwrap_or_else(|| prev.as_ref().map(|m| m.version + 1).unwrap_or(1));
    if let Some(p) = &prev {
        if next <= p.version {
            bail!("a versao do catalogo so cresce (atual {}, pedida {next})", p.version);
        }
    }

    let mut bundles = load_bundles(conn)?;
    // O catalogo vai para todos os usuarios: nada de caminho absoluto do computador do administrador.
    for b in bundles.values_mut() {
        sanitize(b);
    }
    // ---- caminhos (sha -> caminho no Drive, com '/')
    let mut paths: BTreeMap<&str, String> = BTreeMap::new();
    for (sha, b) in &bundles {
        let rel = b.family.rel_path.replace('\\', "/");
        if rel.starts_with('/') || rel.contains(':') || rel.split('/').any(|c| c == ".." || c.is_empty()) {
            bail!("caminho invalido no banco: {rel}");
        }
        paths.insert(sha.as_str(), rel);
    }
    let paths_json = serde_json::to_vec(&paths)?;

    // ---- snapshot: copia compacta do banco + metadados da versao
    let tmp = out.join(format!(".tmp-catalog-{next}.sqlite"));
    let _ = std::fs::remove_file(&tmp);
    let lit = tmp.to_string_lossy().replace('\'', "''");
    conn.execute_batch(&format!("VACUUM INTO '{lit}'")).context("falha ao copiar o banco")?;
    {
        let c2 = Connection::open(&tmp)?;
        {
            let tx = c2.unchecked_transaction()?;
            let mut up = tx.prepare("UPDATE families SET path = ?2, duplicates = ?3 WHERE sha256 = ?1")?;
            for (sha, b) in &bundles {
                up.execute(rusqlite::params![sha, b.family.path, b.family.duplicates])?;
            }
            drop(up);
            tx.commit()?;
        }
        c2.execute_batch("CREATE TABLE IF NOT EXISTS catalog_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);")?;
        c2.execute("INSERT OR REPLACE INTO catalog_meta (key, value) VALUES ('version', ?1)", [next.to_string()])?;
        c2.execute("INSERT OR REPLACE INTO catalog_meta (key, value) VALUES ('generated_at', ?1)", [now_iso()])?;
        c2.execute_batch("VACUUM;")?;
    }
    let snap_gz = gzip(&std::fs::read(&tmp)?)?;
    std::fs::remove_file(&tmp)?;
    let snapshot = write_file(out, &format!("catalog-{next}.sqlite.gz"), &snap_gz)?;
    let paths_ref = write_file(out, "paths.json", &paths_json)?;

    // ---- delta a partir da versao anterior
    let mut deltas: Vec<DeltaRef> = prev.as_ref().map(|m| m.deltas.clone()).unwrap_or_default();
    if let Some(p) = &prev {
        let old_gz = std::fs::read(out.join(&p.snapshot.file)).with_context(|| format!("falta {}", p.snapshot.file))?;
        let td = tempfile::tempdir()?;
        let old_db = td.path().join("old.sqlite");
        std::fs::write(&old_db, gunzip(&old_gz)?)?;
        let old = load_bundles(&Connection::open(&old_db)?)?;
        let delta = make_delta(p.version, next, &old, &bundles);
        let dj = gzip(&serde_json::to_vec(&delta)?)?;
        let name = format!("delta-{}-{}.json.gz", p.version, next);
        let r = write_file(out, &name, &dj)?;
        deltas.push(DeltaRef { from: p.version, to: next, file: r.file, sha256: r.sha256, bytes: r.bytes });
        // mantem so os 12 ultimos deltas (clientes mais atrasados baixam o snapshot)
        while deltas.len() > 12 {
            let old_d = deltas.remove(0);
            let _ = std::fs::remove_file(out.join(old_d.file));
        }
        // snapshots antigos so servem para gerar o proximo delta: guarda so o anterior
        let _ = std::fs::remove_file(out.join(format!("catalog-{}.sqlite.gz", p.version.saturating_sub(1))));
    }

    let manifest = Manifest {
        schema: 1,
        version: next,
        generated_at: now_iso(),
        families: bundles.len() as u32,
        min_plugin: MIN_PLUGIN.to_string(),
        snapshot,
        paths: paths_ref,
        deltas,
    };
    let mj = serde_json::to_vec_pretty(&manifest)?;
    std::fs::write(&manifest_path, &mj)?;
    std::fs::write(out.join("manifest.sig"), sign_manifest(&mj, &key))?;
    Ok(next)
}

/// Confere a assinatura e o hash de cada arquivo citado no manifesto.
pub fn verify_dir(dir: &Path, pubkey_b64: &str) -> Result<Manifest> {
    let mj = std::fs::read(dir.join("manifest.json")).context("sem manifest.json")?;
    let sig = std::fs::read_to_string(dir.join("manifest.sig")).context("sem manifest.sig")?;
    verify_manifest(&mj, &sig, pubkey_b64)?;
    let m: Manifest = serde_json::from_slice(&mj)?;
    let mut files: Vec<(&str, &str)> = vec![(&m.snapshot.file, &m.snapshot.sha256), (&m.paths.file, &m.paths.sha256)];
    for d in &m.deltas {
        files.push((&d.file, &d.sha256));
    }
    for (f, sha) in files {
        let data = std::fs::read(dir.join(f)).with_context(|| format!("falta {f}"))?;
        if sha_hex(&data) != sha {
            bail!("{f}: o hash nao confere com o manifesto");
        }
    }
    Ok(m)
}

/// Quais shas do catalogo nao existem na lista de arquivos do armazenamento (`rclone lsf -R` ou similar).
pub fn missing_in_store(paths_json: &Path, listing: &Path) -> Result<Vec<String>> {
    let paths: BTreeMap<String, String> = serde_json::from_slice(&std::fs::read(paths_json)?)?;
    let text = std::fs::read_to_string(listing)?;
    let have: BTreeSet<String> = text
        .trim_start_matches('\u{feff}') // o PowerShell grava BOM em UTF-8
        .lines()
        .map(|l| l.trim().replace('\\', "/"))
        .filter(|l| !l.is_empty())
        .collect();
    Ok(paths.iter().filter(|(_, p)| !have.contains(p.as_str())).map(|(s, _)| s.clone()).collect())
}

// ---------------------------------------------------------------- testes

#[cfg(test)]
mod tests {
    use super::*;
    use crate::db;

    fn mem() -> Connection {
        let c = Connection::open_in_memory().unwrap();
        c.execute_batch("PRAGMA foreign_keys=ON;").unwrap();
        db::init(&c).unwrap();
        c
    }

    fn add(c: &Connection, n: u32, desc: &str) {
        let sha = format!("{n:064x}");
        c.execute(
            "INSERT INTO families (sha256, path, rel_path, name, size_bytes, folder_category, duplicates, has_preview)
             VALUES (?1, ?2, ?3, ?4, 100, '#PORTAS', '[]', 1)",
            params![sha, format!("F:\\x\\{n}.rfa"), format!("#PORTAS\\porta {n}.rfa"), format!("Porta {n}")],
        )
        .unwrap();
        let id = c.last_insert_rowid();
        c.execute("INSERT INTO previews (family_id, png, width, height, source) VALUES (?1, ?2, 8, 8, 'embedded')", params![id, vec![n as u8; 16]]).unwrap();
        c.execute("INSERT INTO enrichments (family_id, model, data, descricao, categoria, tags) VALUES (?1,'m','{}',?2,'Porta','[\"a\"]')", params![id, desc]).unwrap();
        c.execute("INSERT INTO embeddings (family_id, img, clip_txt, desc_vec) VALUES (?1, ?2, ?2, ?2)", params![id, vec![n as u8; 8]]).unwrap();
    }

    fn key_file(dir: &Path) -> (std::path::PathBuf, String) {
        let p = dir.join("chave.key");
        let pubk = gen_key(&p).unwrap();
        (p, pubk)
    }

    #[test]
    fn assinatura_confere_e_adulteracao_falha() {
        let td = tempfile::tempdir().unwrap();
        let (kf, pubk) = key_file(td.path());
        let key = load_signing_key(&kf).unwrap();
        let sig = sign_manifest(b"{\"version\":1}", &key);
        assert!(verify_manifest(b"{\"version\":1}", &sig, &pubk).is_ok());
        assert!(verify_manifest(b"{\"version\":2}", &sig, &pubk).is_err());
        let (_, outra) = key_file(tempfile::tempdir().unwrap().path());
        assert!(verify_manifest(b"{\"version\":1}", &sig, &outra).is_err());
        assert!(verify_manifest(b"x", "nao-e-base64!!", &pubk).is_err());
    }

    #[test]
    fn nao_sobrescreve_chave_existente() {
        let td = tempfile::tempdir().unwrap();
        let (kf, _) = key_file(td.path());
        assert!(gen_key(&kf).is_err());
    }

    #[test]
    fn delta_aplicado_deixa_o_banco_identico_ao_novo() {
        let a = mem();
        for n in 1..=6 {
            add(&a, n, "descricao");
        }
        let old = load_bundles(&a).unwrap();

        // b = a com: 1 removida, 1 alterada, 2 novas
        let b = mem();
        for n in 2..=6 {
            add(&b, n, if n == 3 { "descricao NOVA" } else { "descricao" });
        }
        add(&b, 7, "nova");
        add(&b, 8, "nova");
        let new = load_bundles(&b).unwrap();

        let delta = make_delta(1, 2, &old, &new);
        assert_eq!(delta.removed, vec![format!("{:064x}", 1)]);
        assert_eq!(delta.upserts.len(), 3, "so a alterada e as 2 novas");

        let mut alvo = mem();
        for n in 1..=6 {
            add(&alvo, n, "descricao");
        }
        apply_delta(&mut alvo, &delta).unwrap();
        assert_eq!(load_bundles(&alvo).unwrap(), new);
        let v: String = alvo.query_row("SELECT value FROM catalog_meta WHERE key='version'", [], |r| r.get(0)).unwrap();
        assert_eq!(v, "2");
    }

    #[test]
    fn delta_sem_mudancas_e_vazio() {
        let a = mem();
        add(&a, 1, "x");
        let m = load_bundles(&a).unwrap();
        let d = make_delta(1, 2, &m, &m);
        assert!(d.removed.is_empty() && d.upserts.is_empty());
    }

    #[test]
    fn delta_com_contagem_errada_e_recusado_e_nada_muda() {
        let mut a = mem();
        add(&a, 1, "x");
        let antes = load_bundles(&a).unwrap();
        let b = mem();
        add(&b, 2, "y");
        let mut d = make_delta(1, 2, &antes, &load_bundles(&b).unwrap());
        d.families_after = 99;
        assert!(apply_delta(&mut a, &d).is_err());
        assert_eq!(load_bundles(&a).unwrap(), antes, "a transacao deveria ter sido desfeita");
    }

    #[test]
    fn exporta_snapshot_manifesto_assinado_e_delta() {
        let td = tempfile::tempdir().unwrap();
        let (kf, pubk) = key_file(td.path());
        let out = td.path().join("catalogo");
        let db1 = mem();
        for n in 1..=4 {
            add(&db1, n, "d");
        }
        assert_eq!(export(&db1, &out, &kf, None).unwrap(), 1);
        let m1 = verify_dir(&out, &pubk).unwrap();
        assert_eq!((m1.version, m1.families, m1.deltas.len()), (1, 4, 0));

        add(&db1, 5, "nova");
        assert_eq!(export(&db1, &out, &kf, None).unwrap(), 2);
        let m2 = verify_dir(&out, &pubk).unwrap();
        assert_eq!((m2.version, m2.families, m2.deltas.len()), (2, 5, 1));
        assert_eq!((m2.deltas[0].from, m2.deltas[0].to), (1, 2));

        // o caminho usa '/' e nunca vaza o caminho do disco do administrador
        let paths: BTreeMap<String, String> = serde_json::from_slice(&std::fs::read(out.join("paths.json")).unwrap()).unwrap();
        assert_eq!(paths.len(), 5);
        assert!(paths.values().all(|p| p.starts_with("#PORTAS/") && !p.contains('\\') && !p.contains(':')));

        // snapshot v2 abre e leva a versao
        let raw = gunzip(&std::fs::read(out.join(&m2.snapshot.file)).unwrap()).unwrap();
        let f = td.path().join("v2.sqlite");
        std::fs::write(&f, raw).unwrap();
        let c = Connection::open(&f).unwrap();
        let v: String = c.query_row("SELECT value FROM catalog_meta WHERE key='version'", [], |r| r.get(0)).unwrap();
        assert_eq!(v, "2");
        assert_eq!(load_bundles(&c).unwrap().len(), 5);
    }

    #[test]
    fn cliente_atrasado_aplica_a_cadeia_de_deltas() {
        let td = tempfile::tempdir().unwrap();
        let (kf, pubk) = key_file(td.path());
        let out = td.path().join("c");
        let base = mem();
        for n in 1..=3 {
            add(&base, n, "d");
        }
        export(&base, &out, &kf, None).unwrap(); // v1
        let cliente_v1 = {
            let m = verify_dir(&out, &pubk).unwrap();
            let f = td.path().join("cliente.sqlite");
            std::fs::write(&f, gunzip(&std::fs::read(out.join(&m.snapshot.file)).unwrap()).unwrap()).unwrap();
            f
        };
        add(&base, 4, "d");
        export(&base, &out, &kf, None).unwrap(); // v2
        base.execute("DELETE FROM families WHERE sha256 = ?1", [format!("{:064x}", 1)]).unwrap();
        add(&base, 5, "d");
        export(&base, &out, &kf, None).unwrap(); // v3

        let m = verify_dir(&out, &pubk).unwrap();
        let mut cli = Connection::open(&cliente_v1).unwrap();
        cli.execute_batch("PRAGMA foreign_keys=ON;").unwrap();
        for d in &m.deltas {
            let delta: Delta = serde_json::from_slice(&gunzip(&std::fs::read(out.join(&d.file)).unwrap()).unwrap()).unwrap();
            apply_delta(&mut cli, &delta).unwrap();
        }
        let mut expected = load_bundles(&base).unwrap();
        expected.values_mut().for_each(sanitize);
        assert_eq!(load_bundles(&cli).unwrap(), expected);
    }

    #[test]
    fn versao_nao_pode_diminuir() {
        let td = tempfile::tempdir().unwrap();
        let (kf, _) = key_file(td.path());
        let out = td.path().join("c");
        let a = mem();
        add(&a, 1, "d");
        export(&a, &out, &kf, Some(5)).unwrap();
        assert!(export(&a, &out, &kf, Some(5)).is_err());
        assert!(export(&a, &out, &kf, Some(3)).is_err());
        assert_eq!(export(&a, &out, &kf, None).unwrap(), 6);
    }

    #[test]
    fn arquivo_adulterado_e_manifesto_adulterado_sao_detectados() {
        let td = tempfile::tempdir().unwrap();
        let (kf, pubk) = key_file(td.path());
        let out = td.path().join("c");
        let a = mem();
        add(&a, 1, "d");
        export(&a, &out, &kf, None).unwrap();
        verify_dir(&out, &pubk).unwrap();

        // 1) paths.json trocado (apontando para outro arquivo) -> hash nao confere
        let p = out.join("paths.json");
        let original = std::fs::read(&p).unwrap();
        std::fs::write(&p, br#"{"x":"outro.rfa"}"#).unwrap();
        assert!(verify_dir(&out, &pubk).is_err());
        std::fs::write(&p, &original).unwrap();
        verify_dir(&out, &pubk).unwrap();

        // 2) manifesto reescrito -> assinatura nao confere
        let mj = std::fs::read_to_string(out.join("manifest.json")).unwrap().replace("\"version\": 1", "\"version\": 9");
        std::fs::write(out.join("manifest.json"), mj).unwrap();
        assert!(verify_dir(&out, &pubk).is_err());
    }

    #[test]
    fn catalogo_nao_leva_caminho_absoluto_do_administrador() {
        let td = tempfile::tempdir().unwrap();
        let (kf, _) = key_file(td.path());
        let a = mem();
        add(&a, 1, "d");
        a.execute(
            "UPDATE families SET path = ?1, duplicates = ?2",
            params![r"F:\Projetos\familias\#PORTAS\porta 1.rfa", r#"["F:\\Projetos\\familias\\copias\\porta 1 (2).rfa","D:\\outro\\x.rfa"]"#],
        )
        .unwrap();
        let out = td.path().join("c");
        export(&a, &out, &kf, None).unwrap();
        let m: Manifest = serde_json::from_slice(&std::fs::read(out.join("manifest.json")).unwrap()).unwrap();
        let raw = gunzip(&std::fs::read(out.join(&m.snapshot.file)).unwrap()).unwrap();
        let f = td.path().join("s.sqlite");
        std::fs::write(&f, &raw).unwrap();
        let c = Connection::open(&f).unwrap();
        let (path, dups): (String, String) = c.query_row("SELECT path, duplicates FROM families", [], |r| Ok((r.get(0)?, r.get(1)?))).unwrap();
        assert_eq!(path, r"#PORTAS\porta 1.rfa");
        let dups: Vec<String> = serde_json::from_str(&dups).unwrap();
        assert_eq!(dups, vec![r"copias\porta 1 (2).rfa".to_string(), "x.rfa".to_string()]);
        // nenhum byte do snapshot contem o caminho do administrador
        let text = String::from_utf8_lossy(&raw);
        assert!(!text.contains(r"F:\Projetos") && !text.contains(r"D:\outro"));
        // e reexportar nao gera delta: a limpeza e idempotente
        export(&a, &out, &kf, None).unwrap();
        let m2: Manifest = serde_json::from_slice(&std::fs::read(out.join("manifest.json")).unwrap()).unwrap();
        let d: Delta = serde_json::from_slice(&gunzip(&std::fs::read(out.join(&m2.deltas[0].file)).unwrap()).unwrap()).unwrap();
        assert!(d.upserts.is_empty() && d.removed.is_empty());
    }

    #[test]
    fn caminho_perigoso_no_banco_bloqueia_a_exportacao() {
        let td = tempfile::tempdir().unwrap();
        let (kf, _) = key_file(td.path());
        for ruim in ["..\\x.rfa", "C:\\x.rfa", "/etc/x.rfa", "a\\\\b.rfa"] {
            let a = mem();
            add(&a, 1, "d");
            a.execute("UPDATE families SET rel_path = ?1", [ruim]).unwrap();
            assert!(export(&a, &td.path().join(format!("o{}", ruim.len())), &kf, None).is_err(), "{ruim}");
        }
    }

    #[test]
    fn missing_in_store_aponta_o_que_falta_no_drive() {
        let td = tempfile::tempdir().unwrap();
        let paths = td.path().join("paths.json");
        std::fs::write(&paths, br##"{"aa":"#PORTAS/a.rfa","bb":"#PORTAS/b.rfa"}"##).unwrap();
        let lst = td.path().join("lista.txt");
        std::fs::write(&lst, "#PORTAS/a.rfa\r\n#OUTRA/z.rfa\r\n").unwrap();
        assert_eq!(missing_in_store(&paths, &lst).unwrap(), vec!["bb".to_string()]);
        // com BOM na primeira linha o resultado nao muda
        std::fs::write(&lst, "\u{feff}#PORTAS/a.rfa\r\n#PORTAS/b.rfa\r\n").unwrap();
        assert!(missing_in_store(&paths, &lst).unwrap().is_empty());
    }
}
