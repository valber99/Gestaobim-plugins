//! Servidor local de busca semantica para o plugin do Revit.
//! Carrega os modelos UMA vez e responde em milissegundos. Escuta so em 127.0.0.1 e exige um token.
//!
//!   GET  /health                       -> {"ok":true,"embeddings":N}
//!   POST /search  {q?, image_b64?, top?, ids?}  -> {"hits":[{"id":..,"score":..}]}
//!   POST /shutdown                     -> encerra o processo
//!
//! Na primeira linha util do stdout escreve `LISTENING <porta>` (o plugin le isso).

use crate::embed::Models;
use crate::search;
use anyhow::{anyhow, Result};
use base64::Engine;
use rusqlite::Connection;
use serde_json::{json, Value};
use std::collections::HashSet;
use std::io::{Read, Write};
use tiny_http::{Header, Method, Request, Response, Server, StatusCode};

fn json_response(code: u16, body: Value) -> Response<std::io::Cursor<Vec<u8>>> {
    Response::from_string(body.to_string())
        .with_status_code(StatusCode(code))
        .with_header(Header::from_bytes("Content-Type", "application/json; charset=utf-8").unwrap())
}

fn authorized(req: &Request, token: &str) -> bool {
    req.headers()
        .iter()
        .any(|h| h.field.equiv("X-GBFam-Token") && h.value.as_str() == token)
}

pub fn run(conn: &Connection, port: u16, token: String, exit_on_stdin_eof: bool) -> Result<()> {
    if exit_on_stdin_eof {
        // O plugin mantem o stdin aberto. Se o Revit morrer, o Windows fecha o pipe, a leitura devolve EOF
        // e o servidor encerra sozinho em vez de ficar orfao ocupando memoria.
        std::thread::spawn(|| {
            let mut buf = [0u8; 256];
            let mut stdin = std::io::stdin();
            loop {
                match stdin.read(&mut buf) {
                    Ok(0) | Err(_) => std::process::exit(0),
                    Ok(_) => {}
                }
            }
        });
    }
    let mut models = Models::load()?;
    let server = Server::http(("127.0.0.1", port)).map_err(|e| anyhow!("nao abriu a porta: {e}"))?;
    let bound = server.server_addr().to_ip().map(|a| a.port()).unwrap_or(port);
    println!("LISTENING {bound}");
    std::io::stdout().flush().ok();

    let embeddings: i64 = conn.query_row("SELECT count(*) FROM embeddings", [], |r| r.get(0))?;

    for mut req in server.incoming_requests() {
        if !authorized(&req, &token) {
            let _ = req.respond(json_response(401, json!({ "error": "token invalido" })));
            continue;
        }
        let (method, url) = (req.method().clone(), req.url().to_string());
        match (method, url.as_str()) {
            (Method::Get, "/health") => {
                let _ = req.respond(json_response(200, json!({ "ok": true, "embeddings": embeddings })));
            }
            (Method::Post, "/shutdown") => {
                let _ = req.respond(json_response(200, json!({ "ok": true })));
                break;
            }
            (Method::Post, "/search") => {
                let mut body = String::new();
                if req.as_reader().take(32 * 1024 * 1024).read_to_string(&mut body).is_err() {
                    let _ = req.respond(json_response(400, json!({ "error": "corpo ilegivel" })));
                    continue;
                }
                let reply = match handle_search(conn, &mut models, &body) {
                    Ok(v) => json_response(200, v),
                    Err(e) => json_response(400, json!({ "error": format!("{e:#}") })),
                };
                let _ = req.respond(reply);
            }
            _ => {
                let _ = req.respond(json_response(404, json!({ "error": "rota desconhecida" })));
            }
        }
    }
    Ok(())
}

fn handle_search(conn: &Connection, models: &mut Models, body: &str) -> Result<Value> {
    let v: Value = serde_json::from_str(body)?;
    let q = v.get("q").and_then(|x| x.as_str());
    let image = match v.get("image_b64").and_then(|x| x.as_str()) {
        Some(b64) => Some(base64::engine::general_purpose::STANDARD.decode(b64)?),
        None => None,
    };
    let top = v.get("top").and_then(|x| x.as_u64()).unwrap_or(100).clamp(1, 2000) as usize;
    let allowed: Option<HashSet<i64>> = v
        .get("ids")
        .and_then(|x| x.as_array())
        .map(|a| a.iter().filter_map(|i| i.as_i64()).collect());
    let ranked = search::rank(conn, models, q, image.as_deref(), allowed.as_ref(), top)?;
    Ok(json!({ "hits": ranked.iter().map(|(id, s)| json!({ "id": id, "score": s })).collect::<Vec<_>>() }))
}
