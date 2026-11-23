Diffault is a local Windows workspace for compiler and runtime diagnostics. Open a build log to inspect grouped findings, or compare builds to see which issues are new, resolved, persistent, or occurring more often.

### In this beta

- Redesigned WPF workspace with a compact sidebar, light and dark themes, a resizable diagnostic inspector, and Ctrl+K commands.
- Local log import by drag and drop, file picker, or clipboard. The engine detects supported formats from the text and handles colored compiler output and MSVC diagnostics.
- Compare two logs or two diagnostic JSON arrays. Select two to eight standalone reports in History to review consecutive transitions without rerunning their original logs.
- Search and filter diagnostics and SQLite history. Inspect numbered excerpts and open the source location in supported external editors.
- Export Markdown, JSON, and SARIF. Exports and reports stay on the computer unless you share them.
- Open About from the sidebar for the installed version, What's New, a manual GitHub Releases check, and the GitHub issue form.

### Install on Windows x64

1. Download the versioned Windows ZIP and `SHA256SUMS.txt`.
2. Compare the SHA-256 hash with `Get-FileHash .\Diffault-v*-win-x64.zip -Algorithm SHA256` in PowerShell.
3. Extract the full archive and run `Diffault.exe` inside its `Diffault` folder.

The self-contained ZIP includes the Rust engine, .NET desktop runtime, and SQLite. Standard user permissions are sufficient. The package is unsigned, and Windows SmartScreen may prompt on first launch.

### Known limits

- Windows x64 is the only packaged target. The portable ZIP does not include a Windows installer or automatic updater.
- The per-log limit is 64 MiB. Unsupported text can show zero diagnostics. File and message changes can appear as separate resolved and new findings.
- Line and column navigation requires a recognized editor executable. Other editors open the file at their default position.
- Compare selected analyses shows consecutive pairs, and export targets the current pair. Reports without a known standalone-analysis type are unavailable for timeline comparison.
- Update checks require network access and can be rate-limited. They open the GitHub release page and leave installation to the user.
