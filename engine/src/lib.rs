use regex::{Captures, Regex};
use serde::{Deserialize, Serialize};
use std::{
    borrow::Cow,
    collections::HashMap,
    ffi::{c_char, c_int},
    sync::LazyLock,
};

unsafe extern "C" {
    fn errorhunt_is_windows_path(bytes: *const c_char, length: usize) -> c_int;
    fn errorhunt_log_encoding(bytes: *const u8, length: usize) -> c_int;
}

#[derive(Clone, Debug, Deserialize, Serialize, PartialEq, Eq)]
pub struct Diagnostic {
    pub language: String,
    pub severity: String,
    pub file: Option<String>,
    pub line: Option<u32>,
    pub column: Option<u32>,
    pub code: Option<String>,
    pub message: String,
    pub original: String,
    pub count: usize,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub previous_count: Option<usize>,
}

static PATTERNS: LazyLock<Vec<(&str, Regex)>> = LazyLock::new(|| {
    [
        ("typescript", r"(?i)^(?<file>.+?)\((?<line>\d+),\s*(?<column>\d+)\):\s*(?<severity>error|warning)\s+(?<code>TS\d+):\s*(?<message>.+)$"),
        ("typescript", r"(?i)^(?<file>.+?):(?<line>\d+):(?<column>\d+)\s*-\s*(?<severity>error|warning)\s+(?<code>TS\d+):\s*(?<message>.+)$"),
        ("csharp", r"(?i)^(?<file>.+?\.cs)\((?<line>\d+),\s*(?<column>\d+)\):\s*(?<severity>error|warning)\s+(?<code>[A-Z]+\d+):\s*(?<message>.+?)(?:\s+\[.+\.csproj\])?$"),
        ("native", r"(?i)^(?<file>.+?\.(?:c|cc|cpp|cxx|h|hpp))\((?<line>\d+)(?:,\s*(?<column>\d+))?\)\s*:\s*(?<severity>fatal error|error|warning)\s+(?<code>C\d+):\s*(?<message>.+)$"),
        ("java", r"(?i)^(?:\[ERROR\]\s+|\[WARNING\]\s+)?(?<file>.+?\.java):(?<line>\d+):\s*(?<severity>error|warning):\s*(?<message>.+)$"),
        ("php", r"(?i)^(?:PHP\s+)?(?<severity>Fatal error|Parse error|Warning|Notice|Deprecated):\s*(?<message>.+?)\s+in\s+(?<file>.+?\.php)\s+on line\s+(?<line>\d+)$"),
        ("lua", r"^(?:lua(?:\.exe)?:\s*)?(?<file>.+?\.lua):(?<line>\d+):\s*(?<message>.+)$"),
        ("native", r"(?i)^(?<file>.+?):(?<line>\d+):(?<column>\d+):\s*(?<severity>fatal error|error|warning|note):\s*(?<message>.+)$"),
        ("go", r"^(?<file>.+?\.go):(?<line>\d+)(?::(?<column>\d+))?:\s*(?<message>.+)$"),
        ("rust", r"^(?:(?<severity>error|warning)\[(?<code>[A-Z]\d+)\]|(?<plain>error|warning)):\s*(?<message>.+)$"),
        ("sql", r"^(?<severity>ERROR|WARNING|FATAL):\s*(?<message>.+)$"),
    ].into_iter().map(|(language, pattern)| (language, Regex::new(pattern).unwrap())).collect()
});
static TERMINAL_CONTROL: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"\x1b\[[0-?]*[ -/]*[@-~]").unwrap());

fn strip_terminal(line: &str) -> Cow<'_, str> {
    if line.contains('\x1b') {
        TERMINAL_CONTROL.replace_all(line, "")
    } else {
        Cow::Borrowed(line)
    }
}

static RUST_LOCATION: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^\s*-->\s+(.+?):(\d+):(\d+)\s*$").unwrap());
static PYTHON_FRAME: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r#"^\s*File "(.+)", line (\d+)(?:, in .+)?$"#).unwrap());
static PYTHON_EXCEPTION: LazyLock<Regex> =
    LazyLock::new(|| Regex::new(r"^[\w.]+(?:Error|Exception|Warning|Exit)(?::.*)?$").unwrap());

fn native_language(file: &str) -> &'static str {
    match file
        .rsplit('.')
        .next()
        .unwrap_or("")
        .to_ascii_lowercase()
        .as_str()
    {
        "c" | "h" => "c",
        "cc" | "cpp" | "cxx" | "hpp" => "cpp",
        "swift" => "swift",
        "rs" => "rust",
        "go" => "go",
        _ => "native",
    }
}

#[derive(Hash, Eq, PartialEq)]
struct Identity {
    language: String,
    severity: String,
    file: String,
    code: String,
    message: String,
}

fn identity(item: &Diagnostic) -> Identity {
    let mut file = item.file.as_deref().unwrap_or("").replace('\\', "/");
    // The classifier reads at most the supplied number of bytes and retains no pointer.
    if unsafe { errorhunt_is_windows_path(file.as_ptr().cast(), file.len()) } != 0 {
        file = file.to_lowercase();
    }
    Identity {
        language: item.language.clone(),
        severity: item.severity.clone(),
        file: file.strip_prefix("./").unwrap_or(&file).to_owned(),
        code: item.code.as_deref().unwrap_or("").to_ascii_uppercase(),
        message: item
            .message
            .split_whitespace()
            .collect::<Vec<_>>()
            .join(" "),
    }
}

#[derive(Default)]
struct Groups {
    diagnostics: Vec<Diagnostic>,
    positions: HashMap<Identity, usize>,
}

impl Groups {
    fn add(&mut self, item: Diagnostic) {
        let key = identity(&item);
        if let Some(&index) = self.positions.get(&key) {
            let count = &mut self.diagnostics[index].count;
            *count = count.saturating_add(item.count);
        } else {
            self.positions.insert(key, self.diagnostics.len());
            self.diagnostics.push(item);
        }
    }

    fn from_items(items: Vec<Diagnostic>) -> Self {
        let mut groups = Self::default();
        for item in items {
            groups.add(item);
        }
        groups
    }
}

pub fn parse(content: &str) -> Vec<Diagnostic> {
    let mut groups = Groups::default();
    let mut lines = content.lines().peekable();
    let mut python_frame = None;
    while let Some(original) = lines.next() {
        let clean = strip_terminal(original);
        let line = clean.trim();
        if line.starts_with("Traceback (most recent call last):") {
            python_frame = None;
            continue;
        }
        if let Some(frame) = PYTHON_FRAME.captures(&clean) {
            python_frame = Some((frame[1].to_owned(), frame[2].parse().ok(), original));
            continue;
        }
        if let Some((file, number, frame)) = python_frame.take() {
            if PYTHON_EXCEPTION.is_match(line) && !clean.starts_with(char::is_whitespace) {
                groups.add(Diagnostic {
                    language: "python".into(),
                    severity: "error".into(),
                    file: Some(file),
                    line: number,
                    column: None,
                    code: None,
                    message: line.into(),
                    original: format!("{frame}\n{original}"),
                    count: 1,
                    previous_count: None,
                });
                continue;
            }
            if clean.starts_with(char::is_whitespace) {
                python_frame = Some((file, number, frame));
                continue;
            }
        }
        for (language, pattern) in PATTERNS.iter() {
            let Some(parts) = pattern.captures(line) else {
                continue;
            };
            let get = |name| parts.name(name).map(|value| value.as_str());
            // A code-less Rust header needs its location to distinguish it from generic logs.
            let rust_location = if *language == "rust" {
                lines.peek().and_then(|next| {
                    let line = strip_terminal(next);
                    let location: Captures<'_> = RUST_LOCATION.captures(&line)?;
                    Some((
                        location[1].to_owned(),
                        location[2].parse().ok(),
                        location[3].parse().ok(),
                    ))
                })
            } else {
                None
            };
            if *language == "rust" && get("code").is_none() && rust_location.is_none() {
                continue;
            }
            let mut file = get("file").map(str::to_owned);
            let mut number = get("line").and_then(|value| value.parse().ok());
            let mut column = get("column").and_then(|value| value.parse().ok());
            let mut source = original.to_owned();
            if let Some((path, row, col)) = rust_location {
                file = Some(path);
                number = row;
                column = col;
                source.push('\n');
                source.push_str(lines.next().unwrap());
            }
            let severity = get("severity")
                .or_else(|| get("plain"))
                .unwrap_or("error")
                .to_ascii_lowercase();
            let severity = match severity.as_str() {
                "fatal" | "fatal error" | "parse error" => "error".into(),
                "deprecated" => "warning".into(),
                other => other.to_owned(),
            };
            groups.add(Diagnostic {
                language: (if *language == "native" {
                    native_language(file.as_deref().unwrap_or(""))
                } else {
                    language
                })
                .into(),
                severity,
                file,
                line: number,
                column,
                code: get("code").map(str::to_owned),
                message: get("message").unwrap().trim().into(),
                original: source,
                count: 1,
                previous_count: None,
            });
            break;
        }
    }
    groups.diagnostics
}

#[derive(Debug, Serialize)]
pub struct Comparison {
    pub fixed: Vec<Diagnostic>,
    pub new: Vec<Diagnostic>,
    pub existing: Vec<Diagnostic>,
}

pub fn compare(before: Vec<Diagnostic>, after: Vec<Diagnostic>) -> Comparison {
    let before = Groups::from_items(before);
    let after = Groups::from_items(after);
    let (existing, new) = after
        .diagnostics
        .into_iter()
        .map(|mut item| {
            item.previous_count = before
                .positions
                .get(&identity(&item))
                .map(|&index| before.diagnostics[index].count);
            item
        })
        .partition(|item| item.previous_count.is_some());
    let fixed = before
        .diagnostics
        .into_iter()
        .filter(|item| !after.positions.contains_key(&identity(item)))
        .map(|mut item| {
            item.previous_count = None;
            item
        })
        .collect();
    Comparison {
        fixed,
        new,
        existing,
    }
}

#[derive(Deserialize)]
pub struct Request {
    pub before: String,
    pub after: Option<String>,
}

pub fn decode(bytes: &[u8]) -> String {
    // The detector only reads BOM bytes when the supplied slice is long enough.
    let endian = match unsafe { errorhunt_log_encoding(bytes.as_ptr(), bytes.len()) } {
        1 => return String::from_utf8_lossy(&bytes[3..]).into_owned(),
        2 => u16::from_le_bytes,
        3 => u16::from_be_bytes,
        _ => return String::from_utf8_lossy(bytes).into_owned(),
    };
    let mut units = bytes[2..].chunks_exact(2);
    let mut decoded: String =
        char::decode_utf16(units.by_ref().map(|pair| endian([pair[0], pair[1]])))
            .map(|unit| unit.unwrap_or(char::REPLACEMENT_CHARACTER))
            .collect();
    if !units.remainder().is_empty() {
        decoded.push('\u{fffd}');
    }
    decoded
}

pub fn decode_owned(bytes: Vec<u8>) -> String {
    if bytes.starts_with(&[0xff, 0xfe])
        || bytes.starts_with(&[0xfe, 0xff])
        || bytes.starts_with(&[0xef, 0xbb, 0xbf])
    {
        return decode(&bytes);
    }
    String::from_utf8(bytes).unwrap_or_else(|error| decode(error.as_bytes()))
}
