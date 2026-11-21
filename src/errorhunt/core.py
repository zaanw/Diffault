"""Parse compiler/runtime logs into structured, comparable diagnostics."""
from __future__ import annotations
from dataclasses import asdict, dataclass
import json
from pathlib import Path
import re
from typing import Iterable

@dataclass(frozen=True)
class Diagnostic:
    language: str
    severity: str
    message: str
    file: str | None = None
    line: int | None = None
    column: int | None = None
    code: str | None = None

    def identity(self) -> tuple[str, str, str, str, str]:
        file = (self.file or "").replace("\\", "/").removeprefix("./")
        if file.startswith("//") or re.match(r"^[a-z]:", file, re.I):
            file = file.casefold()
        return (self.language, self.severity, file, (self.code or "").upper(), " ".join(self.message.split()))

_PATTERNS = [
    ("typescript", re.compile(r"^(?P<file>.+?\.(?:ts|tsx|js|jsx))\((?P<line>\d+),(?P<column>\d+)\):\s*(?P<severity>error|warning)\s+(?P<code>TS\d+):\s*(?P<message>.+)$", re.I)),
    ("typescript", re.compile(r"^(?P<file>.+?\.(?:ts|tsx|js|jsx)):(?P<line>\d+):(?P<column>\d+)\s*-\s*(?P<severity>error|warning)\s+(?P<code>TS\d+):\s*(?P<message>.+)$", re.I)),
    ("csharp", re.compile(r"^(?P<file>.+?\.cs)\((?P<line>\d+),(?P<column>\d+)\):\s*(?P<severity>error|warning)\s+(?P<code>[A-Z]{2}\d+):\s*(?P<message>.+?)(?:\s+\[.+\])?$", re.I)),
    ("native", re.compile(r"^(?P<file>.+?):(?P<line>\d+):(?P<column>\d+):\s*(?P<severity>fatal error|error|warning|note):\s*(?P<message>.+)$", re.I)),
    ("go", re.compile(r"^(?P<file>.+?\.go):(?P<line>\d+):(?P<column>\d+):\s*(?P<message>.+)$")),
    ("java", re.compile(r"^(?P<file>.+?\.java):(?P<line>\d+):\s*(?P<severity>error|warning):\s*(?P<message>.+)$", re.I)),
    ("php", re.compile(r"^(?:PHP\s+)?(?P<severity>Fatal error|Parse error|Warning|Notice):\s*(?P<message>.+?)\s+in\s+(?P<file>.+?\.php)\s+on line\s+(?P<line>\d+)\s*$", re.I)),
    ("lua", re.compile(r"^(?P<file>.+?\.lua):(?P<line>\d+):\s*(?P<message>.+)$")),
    ("python", re.compile(r'^\s*File "(?P<file>.+?\.py)", line (?P<line>\d+)(?:, in .+)?$')),
    ("rust", re.compile(r"^(?P<severity>error|warning)\[(?P<code>[A-Z]\d+)\]:\s*(?P<message>.+)$", re.I)),
    ("sql", re.compile(r"^(?P<severity>ERROR|WARNING|FATAL):\s*(?P<message>.+)$", re.I)),
]

def _language_for_native(filename: str) -> str:
    suffix = Path(filename).suffix.lower()
    return {".c": "c", ".cc": "cpp", ".cpp": "cpp", ".cxx": "cpp", ".h": "c", ".hpp": "cpp", ".swift": "swift", ".rs": "rust"}.get(suffix, "native")

_PYTHON_EXCEPTION = re.compile(r"^[\w.]+(?:Error|Exception|Warning|Exit)(?::.*)?$")


def _position(value: str | None) -> int | None:
    if value is None:
        return None
    try:
        return int(value)
    except ValueError:
        return None


def parse_log(content: str) -> list[Diagnostic]:
    lines = content.splitlines()
    output: list[Diagnostic] = []
    frame: tuple[str, int | None] | None = None
    for i, text in enumerate(lines):
        if text.startswith("Traceback (most recent call last):"):
            frame = None
            continue
        if frame and text and not text[0].isspace():
            if _PYTHON_EXCEPTION.fullmatch(text):
                output.append(Diagnostic("python", "error", text, *frame))
                frame = None
                continue
            frame = None
        for name, pattern in _PATTERNS:
            match = pattern.match(text.strip())
            if match is None:
                continue
            if frame and text[:1].isspace() and name != "python":
                break
            parts = match.groupdict()
            language = _language_for_native(parts.get("file") or "") if name == "native" else name
            message = (parts.get("message") or "").strip()
            severity = (parts.get("severity") or "error").lower()
            if severity in {"fatal error", "parse error"}:
                severity = "error"
            file = parts.get("file")
            line = _position(parts.get("line"))
            column = _position(parts.get("column"))
            code = parts.get("code")
            if name == "python":
                if file is not None:
                    frame = file, line
                break
            elif name == "rust" and file is None:
                location = next((re.search(r"-->\s+(.+?):(\d+):(\d+)", s) for s in lines[i + 1:i + 5] if "-->" in s), None)
                if location:
                    file, line, column = location.group(1), _position(location.group(2)), _position(location.group(3))
            output.append(Diagnostic(language, severity, message, file, line, column, code))
            break
    return list(dict.fromkeys(output))

def compare_logs(before: Iterable[Diagnostic], after: Iterable[Diagnostic]) -> dict[str, list[Diagnostic]]:
    previous = {item.identity(): item for item in before}
    current = {item.identity(): item for item in after}
    return {
        "fixed": [item for key, item in previous.items() if key not in current],
        "new": [item for key, item in current.items() if key not in previous],
        "existing": [item for key, item in current.items() if key in previous],
    }

def json_report(diags: Iterable[Diagnostic]) -> str:
    return json.dumps([asdict(d) for d in diags], ensure_ascii=False, indent=2)
