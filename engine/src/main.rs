use errorhunt_engine::{Diagnostic, Request, compare, decode_owned, parse};
use serde::{Deserialize, de::DeserializeOwned};
use std::{
    env,
    error::Error,
    fs::File,
    io::{self, Read},
    path::Path,
};

const MAX_LOG: usize = 64 * 1024 * 1024;

fn input(path: &str) -> io::Result<String> {
    let file = File::open(Path::new(path))?;
    if !file.metadata()?.is_file() {
        return Err(io::Error::new(
            io::ErrorKind::InvalidInput,
            "Expected a regular log file",
        ));
    }
    let mut bytes = Vec::new();
    file.take((MAX_LOG + 1) as u64).read_to_end(&mut bytes)?;
    if bytes.len() > MAX_LOG {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Log exceeds 64 MiB",
        ));
    }
    if !bytes.starts_with(&[0xff, 0xfe])
        && !bytes.starts_with(&[0xfe, 0xff])
        && bytes[..bytes.len().min(8192)].contains(&0)
    {
        return Err(io::Error::new(
            io::ErrorKind::InvalidData,
            "Unsupported binary log",
        ));
    }
    Ok(decode_owned(bytes))
}

#[derive(Deserialize)]
struct DiagnosticPair {
    before: Vec<Diagnostic>,
    after: Vec<Diagnostic>,
}

fn request<T: DeserializeOwned>(limit: usize) -> Result<T, Box<dyn Error>> {
    let limited = io::stdin().lock().take((limit + 1) as u64);
    let mut input = io::BufReader::new(limited);
    let request = serde_json::from_reader::<_, T>(&mut input)?;
    if input.into_inner().limit() == 0 {
        return Err("JSON request exceeds the supported limit".into());
    }
    Ok(request)
}

fn main() -> Result<(), Box<dyn Error>> {
    let args: Vec<_> = env::args().skip(1).collect();
    let output = io::stdout().lock();
    match args.as_slice() {
        [command, file] if command == "scan" => {
            serde_json::to_writer(output, &parse(&input(file)?))?
        }
        [command, before, after] if command == "compare" => {
            serde_json::to_writer(
                output,
                &compare(parse(&input(before)?), parse(&input(after)?)),
            )?;
        }
        [command, before, after] if command == "compare-reports" => {
            let before = serde_json::from_str(&input(before)?)?;
            let after = serde_json::from_str(&input(after)?)?;
            serde_json::to_writer(output, &compare(before, after))?;
        }
        [command] if command == "stdin" => {
            let request: Request = request(MAX_LOG)?;
            let before = parse(&request.before);
            match request.after {
                Some(after) => serde_json::to_writer(output, &compare(before, parse(&after)))?,
                None => serde_json::to_writer(output, &before)?,
            }
        }
        [command] if command == "compare-stdin" => {
            let request: DiagnosticPair = request(MAX_LOG * 2)?;
            serde_json::to_writer(output, &compare(request.before, request.after))?;
        }
        _ => return Err("Usage: errorhunt-engine scan FILE | compare BEFORE AFTER | compare-reports BEFORE_JSON AFTER_JSON | stdin | compare-stdin".into()),
    }
    Ok(())
}
