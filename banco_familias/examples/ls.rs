use std::collections::BTreeMap;
use std::io::Read;
fn main() {
    let root = std::env::args().nth(1).unwrap();
    let mut heads: BTreeMap<String, (u32, usize)> = BTreeMap::new();
    for e in walkdir::WalkDir::new(root).into_iter().filter_map(|e| e.ok()) {
        let p = e.path();
        if p.extension().map(|x| x.eq_ignore_ascii_case("rfa")) != Some(true) { continue; }
        let Ok(mut c) = cfb::open(p) else { continue };
        let Ok(mut s) = c.open_stream("/RevitPreview4.0") else { continue };
        let mut b = Vec::new(); if s.read_to_end(&mut b).is_err() { continue; }
        let has_png = b.windows(8).any(|w| w == [0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A]);
        if has_png { continue; }
        // procura outras assinaturas
        let sig = |s: &[u8]| b.windows(s.len()).any(|w| w == s);
        let kind = if sig(&[0xFF,0xD8,0xFF]) { "jpeg" } else if sig(b"BM") { "bm(bmp?)" } else if sig(b"GIF8") { "gif" } else { "outro" };
        let key = format!("{kind} len_bucket={}", b.len() / 5000 * 5000);
        let en = heads.entry(key).or_insert((0, 0)); en.0 += 1; en.1 = b.len();
        if kind == "outro" && en.0 == 1 { println!("amostra outro ({} bytes): {:?}", b.len(), &b[..b.len().min(64)]); }
    }
    for (k, v) in heads.iter().take(25) { println!("{:>5}  {k}", v.0); }
}
