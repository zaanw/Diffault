"""Verify an optional adapter and its interoperability with the native engine."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile


def run(command, text):
    return json.loads(
        subprocess.run(
            command, input=text.encode("utf-8"), capture_output=True, check=True, timeout=90
        ).stdout
    )


def fixtures(name, directory):
    if name == "java":
        rows = run(
            ["java", "adapters/JvmDiagnostics.java"],
            '[ERROR] C:\\Проект\\Main.java:[4,9] cannot find symbol\n'
            'Build.java:7: warning: unchecked conversion\n'
            'Exception in thread "main" java.lang.IllegalStateException: café\n'
            '\tat com.example.Main.run(Main.java:18)\n',
        )
        assert [(row["line"], row["column"]) for row in rows] == [(4, 9), (7, None), (18, None)]
        assert rows[2]["code"] == "java.lang.IllegalStateException"
    elif name == "php":
        rows = run(
            [os.getenv("ERRORHUNT_PHP", "php"), "-n", "adapters/php_diagnostics.php"],
            '[14-Nov-2026 12:40:00 UTC] PHP Fatal error: Uncaught RuntimeException: café '
            'in C:\\Проект\\index.php:17\n'
            'Stack trace:\n#0 C:\\Проект\\index.php(8): call()\n'
            'PHP Warning: missing extension\n',
        )
        assert rows[0]["line"] == 17 and rows[0]["code"] == "RuntimeException", rows
        assert "Stack trace:" in rows[0]["original"]
        assert rows[1]["file"] is None and rows[1]["line"] is None
    elif name == "swift":
        rows = run(
            [os.getenv("ERRORHUNT_SWIFT", "swift"), "adapters/swift_diagnostics.swift"],
            'C:\\Проект\\main.swift:7:2: error: café\n'
            '<unknown>: warning: unused option\n',
        )
        assert [(row["line"], row["column"]) for row in rows] == [(7, 2), (None, None)]
        assert rows[0]["file"] == r"C:\Проект\main.swift"
    else:
        rules = directory / "rules.tsv"
        rules.write_text("# Local preferences\nseverity\twarning\nmessage\t[generated]\n", encoding="utf-8")
        rows = [
            dict(language="lua", severity=severity, message=message, code=None, file=None,
                 line=None, column=None, original=message, count=1)
            for severity, message in [
                ("error", "café"),
                ("warning", "café"),
                ("error", "[generated]"),
            ]
        ]
        rows = run([os.getenv("ERRORHUNT_LUA", "lua5.4"), "adapters/filter.lua", str(rules)], json.dumps(rows))
        assert len(rows) == 1 and rows[0]["message"] == "café"
    assert rows and all(row["language"] == name for row in rows)
    return rows


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("engine", type=Path)
    parser.add_argument("adapter", choices=["java", "php", "lua", "swift"])
    args = parser.parse_args()
    with tempfile.TemporaryDirectory() as temp:
        directory = Path(temp)
        rows = fixtures(args.adapter, directory)
        before = directory / "before.json"
        after = directory / "after.json"
        before.write_text(json.dumps(rows + rows[:1]), encoding="utf-8")
        after.write_text(json.dumps(rows), encoding="utf-8")
        result = run([str(args.engine.resolve()), "compare-reports", str(before), str(after)], "")
        assert result == {"fixed": [], "new": [], "existing": rows}, result
    print(f"{args.adapter}: verified adapter output and engine comparison")


if __name__ == "__main__":
    main()
