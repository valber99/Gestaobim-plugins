mod catalog;
mod db;
mod embed;
mod enrich;
mod export;
mod extract;
mod ingest;
mod previews;
mod search;
mod serve;

use anyhow::{bail, Result};
use clap::{Parser, Subcommand};
use std::path::PathBuf;

#[derive(Parser)]
#[command(name = "gbfam", version, about = "Banco de familias Revit: extracao, IA, embeddings e busca")]
struct Cli {
    /// Arquivo SQLite do banco (criado se nao existir)
    #[arg(long, env = "GBFAM_DB", global = true, default_value = r"F:\Projetos\Gestaobim-plugins\banco_familias\gbfam.sqlite")]
    db: PathBuf,
    /// Arquivo .env extra (chave da API do Claude etc.)
    #[arg(long, global = true, default_value = r"F:\Projetos\.env")]
    env_file: PathBuf,
    #[command(subcommand)]
    cmd: Cmd,
}

#[derive(Subcommand)]
enum Cmd {
    /// Cria o arquivo SQLite e as tabelas (tambem acontece automaticamente nos outros comandos)
    Init,
    /// Varre uma pasta de .rfa: extrai preview/metadados e grava no banco (retomavel)
    Ingest {
        #[arg(long, default_value = r"F:\Projetos\Gestaobim-plugins\familias")]
        root: PathBuf,
        #[arg(long, default_value_t = 8)]
        workers: usize,
    },
    /// Gera descricao/categoria/tags com o Claude Haiku 5.5
    Enrich {
        /// Maximo de familias nesta rodada (use 50 para um piloto)
        #[arg(long, default_value_t = 100000)]
        limit: i64,
        #[arg(long, default_value_t = 4)]
        concurrency: usize,
    },
    /// Calcula embeddings CLIP (imagem/texto) e e5 (descricao)
    Embed {
        #[arg(long, default_value_t = 100000)]
        limit: i64,
    },
    /// Busca por texto e/ou imagem
    Search {
        query: Option<String>,
        #[arg(long)]
        image: Option<String>,
        #[arg(long, default_value_t = 10)]
        top: usize,
    },
    /// Lista familias sem preview para o gerador do Revit
    Missing {
        #[arg(long, default_value = "missing.json")]
        out: PathBuf,
    },
    /// Importa PNGs gerados pelo Revit (nome do arquivo = sha256)
    ImportPreviews {
        #[arg(long)]
        dir: PathBuf,
    },
    /// Contagens do banco
    Stats,
    /// Monta o pacote da biblioteca (banco + so as familias unicas) para distribuir com o plugin
    ExportLibrary {
        #[arg(long, default_value = r"F:\Projetos\Gestaobim-plugins\familias")]
        root: PathBuf,
        /// Pasta de saida (precisa estar vazia ou nao existir)
        #[arg(long)]
        out: PathBuf,
        /// So conta e confere, sem copiar
        #[arg(long)]
        dry_run: bool,
    },
    /// Gera a chave de assinatura do catalogo (guarde uma copia fora do computador)
    GenKey {
        #[arg(long)]
        out: PathBuf,
    },
    /// Gera a proxima versao do catalogo (snapshot + delta + paths.json + manifesto assinado)
    ExportCatalog {
        /// Pasta do catalogo (a que o servidor publica)
        #[arg(long)]
        out: PathBuf,
        /// Arquivo da chave de assinatura (de gen-key)
        #[arg(long)]
        key: PathBuf,
        /// Forca um numero de versao (padrao: anterior + 1)
        #[arg(long)]
        version: Option<u32>,
    },
    /// Confere assinatura e hashes de uma pasta de catalogo
    VerifyCatalog {
        #[arg(long)]
        dir: PathBuf,
        /// Chave publica (base64) impressa por gen-key
        #[arg(long)]
        pubkey: String,
    },
    /// Lista o que o catalogo cita e nao existe no armazenamento (entrada: `rclone lsf -R` em arquivo)
    CheckStore {
        #[arg(long)]
        paths: PathBuf,
        #[arg(long)]
        listing: PathBuf,
    },
    /// Servidor local de busca semantica (usado pelo plugin do Revit)
    Serve {
        /// 0 = porta livre qualquer (o numero sai na linha `LISTENING <porta>`)
        #[arg(long, default_value_t = 0)]
        port: u16,
        /// Token exigido no cabecalho X-GBFam-Token
        #[arg(long)]
        token: String,
        /// Encerra quando o stdin fechar (o processo pai morreu)
        #[arg(long)]
        exit_on_stdin_eof: bool,
    },
    /// Teste de extracao SEM banco: percorre .rfa e mostra estatisticas e uma amostra
    Inspect {
        #[arg(long, default_value = r"F:\Projetos\Gestaobim-plugins\familias")]
        root: PathBuf,
        /// Quantos arquivos examinar (0 = todos)
        #[arg(long, default_value_t = 200)]
        sample: usize,
        /// Salva os primeiros N previews nesta pasta para conferir visualmente
        #[arg(long)]
        save_dir: Option<PathBuf>,
    },
}

fn inspect(root: &std::path::Path, sample: usize, save_dir: Option<&std::path::Path>) -> Result<()> {
    use walkdir::WalkDir;
    let files: Vec<_> = WalkDir::new(root)
        .into_iter()
        .filter_map(|e| e.ok())
        .filter(|e| e.file_type().is_file())
        .map(|e| e.into_path())
        .filter(|p| ingest::is_family(p))
        .collect();
    let n = if sample == 0 { files.len() } else { sample.min(files.len()) };
    let step = (files.len() / n.max(1)).max(1);
    let (mut png, mut ver, mut cat, mut err, mut seen) = (0, 0, 0, 0, 0);
    let mut shown = 0;
    if let Some(d) = save_dir {
        std::fs::create_dir_all(d)?;
    }
    for p in files.iter().step_by(step).take(n) {
        seen += 1;
        match extract::extract(p) {
            Ok(e) => {
                if let (Some(bytes), Some(d)) = (&e.png, save_dir) {
                    if shown < 12 {
                        let stem = p.file_stem().unwrap().to_string_lossy();
                        std::fs::write(d.join(format!("{stem}.png")), bytes)?;
                    }
                }
                png += e.png.is_some() as u32;
                ver += e.revit_version.is_some() as u32;
                cat += e.revit_category.is_some() as u32;
                if shown < 5 {
                    shown += 1;
                    println!(
                        "- {}\n    versao={:?} categoria={:?} preview={}\n    partatom={}",
                        p.file_name().unwrap().to_string_lossy(),
                        e.revit_version,
                        e.revit_category,
                        e.png.as_ref().map(|b| format!("{} bytes", b.len())).unwrap_or("nao".into()),
                        e.part_atom.to_string().chars().take(300).collect::<String>()
                    );
                }
            }
            Err(_) => err += 1,
        }
    }
    println!("\nexaminados {seen} de {} .rfa | com preview PNG: {png} | com versao: {ver} | com categoria Revit: {cat} | ilegiveis: {err}", files.len());
    Ok(())
}

#[tokio::main]
async fn main() -> Result<()> {
    let cli = Cli::parse();
    let _ = dotenvy::from_path(&cli.env_file);
    let _ = dotenvy::dotenv();
    if let Cmd::Inspect { root, sample, save_dir } = &cli.cmd {
        return inspect(root, *sample, save_dir.as_deref());
    }
    // Comandos que nao usam o banco (nem o abrem)
    match &cli.cmd {
        Cmd::GenKey { out } => {
            let publica = catalog::gen_key(out)?;
            println!("chave privada gravada em {} (GUARDE UMA COPIA FORA DESTE COMPUTADOR)", out.display());
            println!("chave publica (vai dentro do plugin): {publica}");
            return Ok(());
        }
        Cmd::VerifyCatalog { dir, pubkey } => {
            let m = catalog::verify_dir(dir, pubkey)?;
            println!("catalogo OK: versao {}, {} familias, {} deltas", m.version, m.families, m.deltas.len());
            return Ok(());
        }
        Cmd::CheckStore { paths, listing } => {
            let faltam = catalog::missing_in_store(paths, listing)?;
            println!("{} arquivo(s) do catalogo ausentes no armazenamento", faltam.len());
            for s in faltam.iter().take(20) {
                println!("  {s}");
            }
            if !faltam.is_empty() {
                std::process::exit(2);
            }
            return Ok(());
        }
        _ => {}
    }
    let readonly = matches!(cli.cmd, Cmd::Serve { .. } | Cmd::Search { .. } | Cmd::Stats);
    let conn = if readonly {
        db::open_readonly(&cli.db)?
    } else {
        let c = db::open(&cli.db)?;
        db::init(&c)?; // idempotente: cria as tabelas se ainda nao existem
        c
    };

    match cli.cmd {
        Cmd::Init => println!("banco pronto em {}", cli.db.display()),
        Cmd::Ingest { root, workers } => ingest::run(&conn, &root, workers).await?,
        Cmd::Enrich { limit, concurrency } => enrich::run(&conn, limit, concurrency).await?,
        Cmd::Embed { limit } => embed::run(&conn, limit).await?,
        Cmd::Search { query, image, top } => {
            if query.is_none() && image.is_none() {
                bail!("informe um texto ou --image <arquivo>");
            }
            search::run(&conn, query, image, top)?
        }
        Cmd::Missing { out } => previews::export_missing(&conn, &out)?,
        Cmd::ImportPreviews { dir } => previews::import(&conn, &dir)?,
        Cmd::ExportLibrary { root, out, dry_run } => export::run(&conn, &root, &out, dry_run)?,
        Cmd::GenKey { .. } | Cmd::VerifyCatalog { .. } | Cmd::CheckStore { .. } => unreachable!(),
        Cmd::ExportCatalog { out, key, version } => {
            let v = catalog::export(&conn, &out, &key, version)?;
            println!("catalogo versao {v} gerado em {}", out.display());
        }
        Cmd::Serve { port, token, exit_on_stdin_eof } => serve::run(&conn, port, token, exit_on_stdin_eof)?,
        Cmd::Inspect { .. } => unreachable!(),
        Cmd::Stats => {
            let (f, p, e, m): (i64, i64, i64, i64) = conn.query_row(
                "SELECT (SELECT count(*) FROM families), (SELECT count(*) FROM families WHERE has_preview = 1),
                        (SELECT count(*) FROM enrichments), (SELECT count(*) FROM embeddings)",
                [],
                |r| Ok((r.get(0)?, r.get(1)?, r.get(2)?, r.get(3)?)),
            )?;
            let d: i64 = conn.query_row(
                "SELECT coalesce(sum(json_array_length(duplicates)), 0) FROM families",
                [],
                |r| r.get(0),
            )?;
            println!("familias: {f} | com preview: {p} | enriquecidas: {e} | com embedding: {m} | duplicatas: {d}");
        }
    }
    Ok(())
}
