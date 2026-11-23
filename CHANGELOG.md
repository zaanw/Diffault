# Changelog

## 0.1.0-beta.1

First public beta of the Diffault Windows desktop workspace.

- Added a compact WPF shell with light and dark themes, a resizable diagnostic inspector, keyboard navigation, and a Ctrl+K command palette.
- Added side-by-side log selection, comparison categories and occurrence deltas. History can compare consecutive pairs from two to eight saved standalone analyses.
- Added Markdown export alongside JSON and SARIF. Added line and column arguments for recognized external editors.
- Added the application icon, version display, What's New, manual GitHub update checking and issue reporting.
- Expanded automatic log detection to colored output and MSVC diagnostics. Rejects obvious binary inputs without discarding the current report after a failed import.
- Reduced copies for UTF-8 decoding, UTF-16 conversion, single-group UI results and JSON exports.
- Added a tested, versioned Windows x64 ZIP and SHA-256 checksum. The distribution is portable and unsigned.

See [installation steps](INSTALL.md) and the [release notes](.github/release-notes.md) for supported Windows targets and current limitations.
