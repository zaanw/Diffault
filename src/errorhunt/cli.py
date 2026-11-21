"""Command-line interface for ErrorHunt."""
from __future__ import annotations
import argparse
import json
from dataclasses import asdict
from pathlib import Path
from .core import compare_logs, json_report, parse_log

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(prog="errorhunt", description="Parse and compare build errors")
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
            items = parse_log(args.log.read_text(encoding="utf-8-sig", errors="replace"))
            print(json_report(items) if args.json else "\n".join(f"{d.language} {d.severity} {d.file or '<no file>'}:{d.line or '-'} {d.message}" for d in items))
            return 0
        before = parse_log(args.before.read_text(encoding="utf-8-sig", errors="replace"))
        after = parse_log(args.after.read_text(encoding="utf-8-sig", errors="replace"))
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
        parser.exit(2, f"errorhunt: {exc}\n")

if __name__ == "__main__":
    raise SystemExit(main())
