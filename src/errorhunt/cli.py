"""Command-line interface for Diffault."""
from __future__ import annotations
import argparse
import codecs
import json
import sys
from dataclasses import asdict
from pathlib import Path
from .core import compare_logs, json_report, parse_log

def read_log(path: Path) -> str:
    raw = path.read_bytes()
    encoding = "utf-16" if raw.startswith((codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE)) else "utf-8-sig"
    return raw.decode(encoding, errors="replace")


def main(argv: list[str] | None = None) -> int:
    invoked_as = Path(sys.argv[0]).stem.casefold()
    prog = "errorhunt" if invoked_as == "errorhunt" else "diffault"
    parser = argparse.ArgumentParser(prog=prog, description="Parse and compare build errors")
    commands = parser.add_subparsers(dest="command", required=True)
    scan = commands.add_parser("scan", help="Parse a log into structured diagnostics")
    scan.add_argument("log", type=Path)
    scan.add_argument("--json", action="store_true", help="Emit machine-readable JSON")
    diff = commands.add_parser("compare", help="Compare two build logs")
    diff.add_argument("before", type=Path)
    diff.add_argument("after", type=Path)
    diff.add_argument("--json", action="store_true")
    args = parser.parse_args(argv)
    try:
        if args.command == "scan":
            items = parse_log(read_log(args.log))
            print(json_report(items) if args.json else "\n".join(f"{d.language} {d.severity} {d.file or '<no file>'}:{d.line or '-'} {d.message}" for d in items))
            return 0
        before = parse_log(read_log(args.before))
        after = parse_log(read_log(args.after))
        changes = compare_logs(before, after)
        if args.json:
            print(json.dumps({k: [asdict(d) for d in v] for k, v in changes.items()}, indent=2, ensure_ascii=False))
        else:
            for name, values in changes.items():
                print(f"{name.upper()}: {len(values)}")
                for d in values:
                    print(f"  {d.file or '<no file>'}:{d.line or '-'} {d.message}")
        return 0
    except OSError as exc:
        parser.exit(2, f"{parser.prog}: {exc}\n")

if __name__ == "__main__":
    raise SystemExit(main())
