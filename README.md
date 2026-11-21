# ErrorHunt 🐛

**Find errors. Track fixes. Ship code.**

ErrorHunt is a local-first tool for extracting compiler/runtime diagnostics from logs and comparing two builds. The current implementation is a **Python CLI alpha**, not yet a Windows desktop app.

## Quick start

Requires Python 3.10+:

```powershell
git clone https://github.com/zaanw/ErrorHunt.git
cd ErrorHunt
python -m pip install -e .
errorhunt scan build.log
errorhunt scan build.log --json
errorhunt compare before.log after.log
errorhunt compare before.log after.log --json
```

## Supported log patterns

Initial rules for **C#, Java, C, C++, TypeScript, Python, Rust, Go, SQL, PHP, Lua and Swift**. Support is limited to common diagnostic layouts and is not a guarantee of exhaustive detection. Generic SQL messages can be ambiguous without context.

## How comparisons work

Matches use language, severity, source file, error code and text. Moving an error to a new line does not automatically create a new error. File renames and wording changes can be reported as fixed + new. Duplicate identical lines are collapsed. ErrorHunt does **not** automatically repair code or claim to find the root cause.

## Tests

```powershell
python -m pip install pytest
python -m pytest -q
```

GitHub Actions runs tests on Windows for pushes and pull requests.

## Next milestones

1. Test with real compiler logs and harden parsers.
2. Build the Windows desktop user interface.
3. Add source navigation, SARIF export and richer comparisons.
4. Introduce additional language implementations only when their modules have useful jobs.

We plan a modular, polyglot architecture, but **only Python is currently implemented**.

## License

MIT (see LICENSE).
