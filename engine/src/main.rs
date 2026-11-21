use errorhunt_engine::{Request, compare, decode, parse};
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
    Ok(decode(&bytes))
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
            let mut bytes = Vec::new();
            io::stdin()
                .take((MAX_LOG + 1) as u64)
                .read_to_end(&mut bytes)?;
            if bytes.len() > MAX_LOG {
                return Err("JSON request exceeds 64 MiB".into());
            }
            let request: Request = serde_json::from_slice(&bytes)?;
            let before = parse(&request.before);
            match request.after {
                Some(after) => serde_json::to_writer(output, &compare(before, parse(&after)))?,
                None => serde_json::to_writer(output, &before)?,
            }
        }
        _ => return Err("Usage: errorhunt-engine scan FILE | compare BEFORE AFTER | compare-reports BEFORE_JSON AFTER_JSON | stdin".into()),
    }
    Ok(())
}
