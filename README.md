# Diffault

Diffault reads compiler and runtime logs locally. Compare two builds to identify diagnostics that were fixed, newly introduced, or are still present.

## Windows desktop

The Windows x64 build contains the WPF application, Rust engine, .NET runtime and SQLite. Extract the entire `Diffault-win-x64` archive into one directory and run `Diffault.exe`. The program runs offline and does not require Python or build tools.

- In **Compare**, select **Before** and **After**, then click **Compare logs**. You can also drop two logs on the window and confirm their order. Select **Fixed**, **New** or **Existing** to inspect each group.
- In **Analyze**, open or drop one log, or select **Analyze clipboard**. Select a diagnostic to see the log text that produced it. Search by file, code or message.
- Export the current report to **JSON** or **SARIF**. Open saved reports from **History**. Settings lets you disable saving future analyses and choose the preferred export format. Choose a local project folder and an editor in **Settings** to enable **Open source** from a selected diagnostic. Existing history stays in `%LOCALAPPDATA%\ErrorHunt\history.db`.

The engine supports common formats for C, C++, C#, Go, Java, Lua, PHP, Python, Rust, SQL, Swift and TypeScript. Log files can be UTF-8 (with or without BOM) or BOM-marked UTF-16. Invalid byte sequences are replaced. The engine accepts files up to 64 MiB per log and displays positions only when captured from the log.

Duplicates are grouped by language, severity, normalized path, code and message whitespace. The report shows a count and retains the first displayed source location for each group. **Existing** also shows the count from the earlier log. Select **More frequent** to find issues that appeared more times in the second build. The offline viewer offers the same filter and displays counts as `before → after`. Comparisons ignore changes to line and column. A file rename or wording change can appear as **Fixed** plus **New** because the engine cannot establish that they refer to the same diagnostic. Standalone scans use the **New** group so scans and comparisons share an export format.

## Build the Windows x64 app

Use the .NET 10 SDK, Rust 1.100.0 and the Visual Studio Build Tools C++ toolchain with a Windows SDK. Run from the repository root in PowerShell.

```powershell
rustup toolchain install 1.100.0 --profile minimal
cargo +1.100.0 test --locked --manifest-path engine/Cargo.toml
cargo +1.100.0 build --release --locked --manifest-path engine/Cargo.toml --target x86_64-pc-windows-msvc
$engine = (Resolve-Path engine/target/x86_64-pc-windows-msvc/release/errorhunt-engine.exe).Path
dotnet run --project checks/ErrorHunt.Checks.csproj -p:RestoreLockedMode=true -- $engine
dotnet publish desktop/ErrorHunt.Desktop.csproj -c Release -r win-x64 --self-contained -p:RestoreLockedMode=true "-p:EngineExecutable=$engine" -o artifacts/win-x64
.\artifacts\win-x64\Diffault.exe
```

The Diffault Desktop workflow performs the build and uploads the self-contained application directory as an Actions artifact. The integration check runs the supplied engine against Unicode logs, verifies the three categories, checks JSON and SARIF output and reopens the saved report from SQLite.

## Python compatibility CLI

Requires Python 3.10 or newer.

```sh
python -m pip install -e . pytest
python -m pytest -q
diffault scan build.log --json
diffault compare before.log after.log --json
```

The `errorhunt` command is also available for existing scripts.

## Batch processing and offline viewer

The Go CLI scans several logs concurrently and emits one JSON array in argument order. Each entry contains a file and its diagnostics or its error. The exit status is nonzero if any scan fails. With Go 1.25 or later, run from the repository root after building the Rust engine for your platform.

```sh
cd batch
go run . -engine ../engine/target/debug/errorhunt-engine -jobs 4 first.log second.log
ERRORHUNT_TEST_ENGINE="$(cd ../engine/target/debug && pwd)/errorhunt-engine" go test -v ./...
```

For the browser viewer, run `npm ci` and `npm test` from `viewer/`. Open the generated standalone `viewer/dist/index.html` in a browser and choose an exported comparison JSON file. The viewer filters and paginates locally using that file. It also accepts the JSON output of `errorhunt-engine compare BEFORE AFTER`.

## Component protocol

WPF and the Go CLI pass arguments to the Rust engine without a shell. The engine writes UTF-8 JSON to stdout and returns a nonzero exit code on input errors.

| Command | Output |
| --- | --- |
| `scan FILE` | Diagnostic array |
| `compare BEFORE AFTER` | Object with `fixed`, `new`, and `existing` diagnostic arrays |
| `compare-reports BEFORE_JSON AFTER_JSON` | The same comparison object, using diagnostic arrays produced by adapters or previous scans |
| `stdin` | Accepts a JSON object with `before` log text and optional `after` log text. Returns the same shape as scan or compare. |

Each diagnostic contains `language`, `severity`, `file`, `line`, `column`, `code`, `message`, `original` and `count`. Existing diagnostics from a comparison also include `previous_count` in JSON and in the SARIF result properties. Older reports may omit the earlier count. Unavailable location fields and codes are null. The Rust engine calls the C encoding detector and the C++ Windows path classifier through a narrow C ABI. The WPF reporting library uses `reports/history.sql` to initialize its SQLite database.

## Optional adapters and filtering

The desktop app handles common formats directly. For additional Maven and JVM stack trace formats, PHP runtime logs or Swift driver diagnostics, run the corresponding adapter explicitly with a UTF-8 log on stdin. The WPF **Compare diagnostic JSON** action accepts the resulting arrays. The engine command `compare-reports` does the same on the command line.

| Tool | Example |
| --- | --- |
| Java 21 | `java adapters/JvmDiagnostics.java < build.log > diagnostics.json` |
| PHP 8.2+ | `php -n adapters/php_diagnostics.php < php.log > diagnostics.json` |
| Swift 6 | `swift adapters/swift_diagnostics.swift < swift.log > diagnostics.json` |
| Lua 5.4 with lua-cjson | `lua5.4 adapters/filter.lua local-rules.tsv < diagnostics.json > filtered.json` |

The Lua filter removes a diagnostic if any rule's case-sensitive literal matches the specified field. Put one field and literal per line, separated by a tab. Supported fields are language, severity, file, code and message. Lines starting with `#` are comments. The rules file contains data and is never executed as Lua.

Run `python3 adapters/check.py engine/target/debug/errorhunt-engine java` to test Java against the local engine. Substitute `php`, `swift` or `lua` to test each adapter with the corresponding runtime installed.

See [LICENSE](LICENSE) for the MIT license.
