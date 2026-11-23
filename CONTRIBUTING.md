# Contributing to Diffault

Use [GitHub Issues](https://github.com/zaanw/Diffault/issues) to report a reproducible problem or propose a change. Use synthetic or scrubbed diagnostic examples. Avoid posting project paths, raw logs, credentials or personal data.

The [build guide](README.md#build-the-windows-x64-app) lists the Windows prerequisites. The Rust engine, Python compatibility CLI, Go batch runner and TypeScript viewer can also be tested on Linux. Commit lockfile updates with dependency changes.

Before opening a pull request:

- Run the relevant tests and describe the scenarios covered.
- Check `cargo fmt --manifest-path engine/Cargo.toml --all -- --check` for Rust changes.
- For WPF changes, verify both themes and a compact window when Windows is available. Include screenshots taken from the application if the layout changes.
- Keep existing report schemas and SQLite history readable. Add regression coverage for changes to parsers, export formats or settings.

The Desktop workflow audits dependencies, verifies the Windows ZIP and checks Rust, .NET, Python, Go, TypeScript and the optional adapters. Tag-based beta releases use the exact version in `VERSION` and publish only after the required jobs pass.
