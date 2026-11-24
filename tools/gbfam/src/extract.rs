//! Leitura dos streams OLE2 (MS-CFB) de um .rfa: preview PNG, BasicFileInfo e PartAtom.

use anyhow::{Context, Result};
use serde_json::{json, Value};
use std::io::Read;
use std::path::Path;

const PNG_SIG: [u8; 8] = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

#[derive(Default)]
pub struct Extracted {
    pub png: Option<Vec<u8>>,
    pub revit_version: Option<String>,
    pub revit_build: Option<String>,
    pub revit_category: Option<String>,
    pub basic_info: Value,
    pub part_atom: Value,
}

pub fn extract(path: &Path) -> Result<Extracted> {
    let mut comp = cfb::open(path).with_context(|| format!("OLE2 invalido: {}", path.display()))?;
    let mut out = Extracted::default();

    if let Some(bytes) = read_stream(&mut comp, "RevitPreview4.0") {
        out.png = carve_png(&bytes);
    }
    if let Some(bytes) = read_stream(&mut comp, "BasicFileInfo") {
        let (info, version, build) = parse_basic_info(&bytes);
        out.basic_info = info;
        out.revit_version = version;
        out.revit_build = build;
    }
    if let Some(bytes) = read_stream(&mut comp, "PartAtom") {
        let (atom, category) = parse_part_atom(&bytes);
        out.part_atom = atom;
        out.revit_category = category;
    }
    Ok(out)
}

fn read_stream<F: Read + std::io::Seek + std::io::Write>(
    comp: &mut cfb::CompoundFile<F>,
    name: &str,
) -> Option<Vec<u8>> {
    let mut s = comp.open_stream(format!("/{name}")).ok()?;
    let mut buf = Vec::new();
    s.read_to_end(&mut buf).ok()?;
    Some(buf)
}

/// Extrai a imagem do stream de preview e devolve sempre PNG.
/// Revit novo grava PNG; os mais antigos gravam JPEG (~1/3 da biblioteca).
fn carve_png(data: &[u8]) -> Option<Vec<u8>> {
    if let Some(start) = find(data, &PNG_SIG, 0) {
        let end = match find(data, b"IEND", start) {
            Some(i) => (i + 8).min(data.len()),
            None => data.len(),
        };
        let png = data[start..end].to_vec();
        // Valida: precisa decodificar de fato.
        if image::load_from_memory_with_format(&png, image::ImageFormat::Png).is_ok() {
            return Some(png);
        }
    }
    // JPEG: de FF D8 FF ate o ultimo FF D9.
    let start = find(data, &[0xFF, 0xD8, 0xFF], 0)?;
    let end = data[start..]
        .windows(2)
        .rposition(|w| w == [0xFF, 0xD9])
        .map(|p| start + p + 2)?;
    let img = image::load_from_memory_with_format(&data[start..end], image::ImageFormat::Jpeg).ok()?;
    let mut out = std::io::Cursor::new(Vec::new());
    img.write_to(&mut out, image::ImageFormat::Png).ok()?;
    Some(out.into_inner())
}

fn find(hay: &[u8], needle: &[u8], from: usize) -> Option<usize> {
    if hay.len() < needle.len() || from > hay.len() - needle.len() {
        return None;
    }
    hay[from..]
        .windows(needle.len())
        .position(|w| w == needle)
        .map(|p| p + from)
}

/// BasicFileInfo e texto UTF-16LE com linhas `Chave: valor`.
/// So guardamos chaves de whitelist: usuario e caminhos locais ficam de fora (privacidade).
fn parse_basic_info(bytes: &[u8]) -> (Value, Option<String>, Option<String>) {
    let units: Vec<u16> = bytes
        .chunks_exact(2)
        .map(|c| u16::from_le_bytes([c[0], c[1]]))
        .collect();
    let text = String::from_utf16_lossy(&units);
    let text: String = text
        .chars()
        .map(|c| if c.is_control() && c != '\n' && c != '\r' { '\n' } else { c })
        .collect();

    let mut info = serde_json::Map::new();
    let mut version = None;
    let mut build = None;
    for line in text.split(['\n', '\r']) {
        let line = line.trim();
        if let Some((k, v)) = line.split_once(':') {
            let (k, v) = (k.trim(), v.trim());
            match k {
                "Format" if !v.is_empty() => {
                    version = Some(v.to_string());
                    info.insert("format".into(), json!(v));
                }
                "Revit Build" | "Build" if !v.is_empty() => {
                    build = Some(v.to_string());
                    info.insert("revit_build".into(), json!(v));
                }
                "Worksharing" if !v.is_empty() => {
                    info.insert("worksharing".into(), json!(v));
                }
                _ => {}
            }
        }
    }
    // Arquivos antigos nao tem "Format:"; a versao vem na string "Autodesk Revit ... 20xx (Build: ...)".
    if version.is_none() {
        if let Some(i) = text.find("Autodesk Revit") {
            let product: String = text[i..].chars().take_while(|c| *c != '(' && *c != '\n' && *c != '\r').collect();
            let product = product.trim().to_string();
            version = product
                .split_whitespace()
                .find(|w| w.len() == 4 && w.starts_with("20") && w.chars().all(|c| c.is_ascii_digit()))
                .map(String::from);
            info.insert("produto".into(), json!(product));
        }
    }
    // Ultimo recurso: ano do build (formato AAAAMMDD_HHMM).
    if version.is_none() {
        if let Some(b) = &build {
            if b.len() >= 8 && b[..8].chars().all(|c| c.is_ascii_digit()) {
                info.insert("versao_estimada_pelo_build".into(), json!(&b[..4]));
            }
        }
    }
    (Value::Object(info), version, build)
}

/// PartAtom e um XML (Atom) com metadados da familia. Fazemos uma varredura simples:
/// guardamos textos de elementos relevantes e o atributo `term` de `<category>`.
fn parse_part_atom(bytes: &[u8]) -> (Value, Option<String>) {
    let start = bytes.iter().position(|&b| b == b'<').unwrap_or(0);
    let xml = String::from_utf8_lossy(&bytes[start..]);
    let mut title = None;
    let mut categories: Vec<String> = Vec::new();
    let mut texts: Vec<(String, String)> = Vec::new();

    let mut rest: &str = &xml;
    while let Some(lt) = rest.find('<') {
        rest = &rest[lt + 1..];
        let Some(gt) = rest.find('>') else { break };
        let tag = &rest[..gt];
        let after = &rest[gt + 1..];
        if tag.starts_with('/') || tag.starts_with('?') || tag.starts_with('!') {
            continue;
        }
        let name_end = tag
            .find(|c: char| c.is_whitespace() || c == '/')
            .unwrap_or(tag.len());
        let full = &tag[..name_end];
        let local = full.rsplit(':').next().unwrap_or(full);

        if local == "category" {
            if let Some(term) = attr(tag, "term") {
                if !term.is_empty() && !categories.contains(&term) {
                    categories.push(term);
                }
            }
        }
        // Texto ate o proximo '<'.
        if !tag.ends_with('/') {
            let text_end = after.find('<').unwrap_or(after.len());
            let text = after[..text_end].trim();
            if !text.is_empty() && text.len() < 300 {
                if local == "title" && title.is_none() {
                    title = Some(text.to_string());
                }
                if texts.len() < 80 && !texts.iter().any(|(k, v)| k == local && v == text) {
                    texts.push((local.to_string(), text.to_string()));
                }
            }
        }
    }

    // Categoria: `<category><term>Generic Models</term>` (ignora as taxonomias `adsk:...`).
    let category = categories
        .iter()
        .find(|c| !c.starts_with("adsk:"))
        .cloned()
        .or_else(|| {
            texts
                .iter()
                .find(|(k, v)| k == "term" && !v.starts_with("adsk:"))
                .map(|(_, v)| v.clone())
        });
    let texts_json: Vec<Value> = texts.iter().map(|(k, v)| json!({ "campo": k, "valor": v })).collect();
    (
        json!({ "title": title, "categories": categories, "texts": texts_json }),
        category,
    )
}

fn attr(tag: &str, name: &str) -> Option<String> {
    let key = format!("{name}=");
    let i = tag.find(&key)?;
    let rest = &tag[i + key.len()..];
    let quote = rest.chars().next()?;
    if quote != '"' && quote != '\'' {
        return None;
    }
    let inner = &rest[1..];
    let end = inner.find(quote)?;
    Some(inner[..end].to_string())
}
