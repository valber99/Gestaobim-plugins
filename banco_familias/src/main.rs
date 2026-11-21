mod db;
mod embed;
mod enrich;
mod extract;
mod ingest;
mod previews;
mod search;

use anyhow::{bail, Result};
use clap::{Parser, Subcommand};
use sqlx::Row;
use std::path::PathBuf;

#[derive(Parser)]
#[command(name = "gbfam", about = "Banco de familias Revit: extracao, IA, embeddings e busca")]
struct Cli {
    /// URL do Postgres. Ex.: postgres://usuario:senha@localhost:5433/gbfamilias
    #[arg(long, env = "DATABASE_URL", global = true, hide_env_values = true)]
    database_url: Option<String>,
    /// Arquivo .env extra (chave da API do Claude etc.)
    #[arg(long, global = true, default_value = r"F:\Projetos\.env")]
    env_file: PathBuf,
    #[command(subcommand)]
    cmd: Cmd,
}

#[derive(Subcommand)]
enum Cmd {
    /// Cria extensao pgvector, tabelas e indices
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
    let url = cli.database_url.clone().or_else(|| std::env::var("DATABASE_URL").ok());
    let Some(url) = url else {
        bail!("defina DATABASE_URL (ex.: postgres://usuario:senha@localhost:5433/gbfamilias) ou use --database-url");
    };
    let pool = db::connect(&url).await?;

    match cli.cmd {
        Cmd::Init => {
            db::init(&pool).await?;
            println!("schema pronto");
        }
        Cmd::Ingest { root, workers } => ingest::run(&pool, &root, workers).await?,
        Cmd::Enrich { limit, concurrency } => enrich::run(&pool, limit, concurrency).await?,
        Cmd::Embed { limit } => embed::run(&pool, limit).await?,
        Cmd::Search { query, image, top } => {
            if query.is_none() && image.is_none() {
                bail!("informe um texto ou --image <arquivo>");
            }
            search::run(&pool, query, image, top).await?
        }
        Cmd::Missing { out } => previews::export_missing(&pool, &out).await?,
        Cmd::ImportPreviews { dir } => previews::import(&pool, &dir).await?,
        Cmd::Inspect { .. } => unreachable!(),
        Cmd::Stats => {
            let r = sqlx::query(
                "SELECT (SELECT count(*) FROM families) f, (SELECT count(*) FROM families WHERE has_preview) p,
                        (SELECT count(*) FROM enrichments) e, (SELECT count(*) FROM embeddings) m,
                        (SELECT coalesce(sum(cardinality(duplicates)),0) FROM families) d",
            )
            .fetch_one(&pool)
            .await?;
            println!(
                "familias: {} | com preview: {} | enriquecidas: {} | com embedding: {} | duplicatas: {}",
                r.get::<i64, _>("f"),
                r.get::<i64, _>("p"),
                r.get::<i64, _>("e"),
                r.get::<i64, _>("m"),
                r.get::<i64, _>("d")
            );
        }
    }
    Ok(())
}
