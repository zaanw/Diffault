# Install Diffault

Diffault is distributed as a portable ZIP for Windows x64. It contains the .NET desktop runtime, the Rust engine and SQLite.

1. Open [GitHub Releases](https://github.com/zaanw/Diffault/releases) and download the versioned `Diffault-v…-win-x64.zip` and `SHA256SUMS.txt` from the same release.
2. In PowerShell, check that the hash matches the entry for the ZIP in `SHA256SUMS.txt`.

   ```powershell
   Get-FileHash .\Diffault-v0.1.0-beta.1-win-x64.zip -Algorithm SHA256
   ```

3. Select **Extract All**. Open the extracted `Diffault` folder and run `Diffault.exe`. Keep its companion files in that folder.

Windows may display a SmartScreen prompt because this beta is unsigned. Verify the download came from this repository before running it. Diffault runs under a standard Windows account.

## Updating or removing Diffault

Close Diffault and extract the newer ZIP to a separate folder before launching it. **About Diffault → Check for updates** checks GitHub on request and opens the release page when a compatible build is available.

Settings and saved reports live in `%LOCALAPPDATA%\ErrorHunt`. The existing name preserves compatibility with earlier builds. You can back up that directory while Diffault is closed. Removing the extracted app folder leaves your settings and reports in place.

## Startup troubleshooting

Run the extracted `Diffault.exe` rather than opening it inside the ZIP. If the window still fails to open, extract a fresh copy and check `%LOCALAPPDATA%\ErrorHunt\diagnostics.log`. A Windows loader failure may occur before the app can write a diagnostic entry.
