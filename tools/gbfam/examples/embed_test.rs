use fastembed::*;
fn cos(a: &[f32], b: &[f32]) -> f32 {
    let d: f32 = a.iter().zip(b).map(|(x, y)| x * y).sum();
    d / (a.iter().map(|x| x * x).sum::<f32>().sqrt() * b.iter().map(|x| x * x).sum::<f32>().sqrt())
}
fn main() {
    let dir = std::path::PathBuf::from(".models");
    let mut img = ImageEmbedding::try_new(ImageInitOptions::new(ImageEmbeddingModel::ClipVitB32).with_cache_dir(dir.clone())).unwrap();
    let mut txt = TextEmbedding::try_new(TextInitOptions::new(EmbeddingModel::ClipVitB32).with_cache_dir(dir.clone())).unwrap();
    let mut e5 = TextEmbedding::try_new(TextInitOptions::new(EmbeddingModel::MultilingualE5Small).with_cache_dir(dir)).unwrap();
    let png = std::fs::read(std::env::args().nth(1).unwrap()).unwrap();
    let iv = img.embed_bytes(&[png.as_slice()], None).unwrap().remove(0);
    println!("dim imagem={}", iv.len());
    for q in ["a sofa", "a door", "a tree", "a bathroom faucet", "um sofa de tres lugares", "uma porta de madeira"] {
        let tv = txt.embed(vec![q], None).unwrap().remove(0);
        println!("CLIP img~'{q}': {:.4}", cos(&iv, &tv));
    }
    let docs = ["passage: Sofa de tres lugares estofado para sala de estar", "passage: Porta de madeira de abrir com batente", "passage: Arvore ornamental para paisagismo"];
    let q = e5.embed(vec!["query: sofa para sala"], None).unwrap().remove(0);
    println!("dim e5={}", q.len());
    for d in docs { let dv = e5.embed(vec![d], None).unwrap().remove(0); println!("e5 'sofa para sala' ~ '{d}': {:.4}", cos(&q, &dv)); }
}
