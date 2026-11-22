param(
    [Parameter(Mandatory)][string] $PublishDirectory,
    [Parameter(Mandatory)][string] $ArchivePath,
    [Parameter(Mandatory)][string] $Screenshots
)

$ErrorActionPreference = 'Stop'
$PublishDirectory = (Resolve-Path -LiteralPath $PublishDirectory).Path
$ArchivePath = [System.IO.Path]::GetFullPath($ArchivePath)
$Screenshots = [System.IO.Path]::GetFullPath($Screenshots)
$root = [System.IO.Directory]::CreateTempSubdirectory('Diffault package ').FullName

function Wait-ForDiffaultWindow($Process) {
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    do {
        $Process.Refresh()
        if ($Process.HasExited) {
            throw "Diffault quit during normal startup with exit code $($Process.ExitCode)."
        }
        if ($Process.MainWindowHandle -ne 0 -and $Process.MainWindowTitle -eq 'Diffault') { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The normal launch did not create the Diffault window.'
}

try {
    $staging = Join-Path $root 'Diffault'
    $extracted = Join-Path $root 'Extracted package'
    $unrelatedDirectory = Join-Path $root 'Empty working directory'
    New-Item -ItemType Directory -Path $staging, $extracted, $unrelatedDirectory | Out-Null
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $ArchivePath) | Out-Null
    Copy-Item -Path (Join-Path $PublishDirectory '*') -Destination $staging -Recurse
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../../LICENSE') -Destination (Join-Path $staging 'LICENSE.txt')
    @'
DIFFAULT FOR WINDOWS X64

1. Extract the entire ZIP using Extract All.
2. Open the extracted Diffault folder and run Diffault.exe.
3. Keep the files in the Diffault folder together. The package includes the .NET desktop runtime and the analysis engine.

Diffault runs with normal user permissions and keeps reports on your computer.
If startup fails, check %LOCALAPPDATA%\ErrorHunt\diagnostics.log.
'@ | Set-Content -LiteralPath (Join-Path $staging 'START HERE.txt') -Encoding UTF8

    Compress-Archive -LiteralPath $staging -DestinationPath $ArchivePath -Force
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $extracted
    $appFolder = Join-Path $extracted 'Diffault'
    foreach ($name in @('Diffault.exe', 'Diffault.dll', 'Diffault.deps.json', 'Diffault.runtimeconfig.json',
            'errorhunt-engine.exe', 'hostfxr.dll', 'hostpolicy.dll', 'coreclr.dll', 'wpfgfx_cor3.dll',
            'PresentationFramework.dll', 'PresentationFramework.Fluent.dll', 'e_sqlite3.dll', 'START HERE.txt')) {
        $file = Join-Path $appFolder $name
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing package file: $name" }
    }
    $packagedFiles = @(Get-ChildItem -LiteralPath $appFolder -File -Recurse)
    $stagedFiles = @(Get-ChildItem -LiteralPath $staging -File -Recurse)
    if ($packagedFiles.Count -ne $stagedFiles.Count) { throw 'The archive file count is incorrect.' }
    foreach ($file in $stagedFiles) {
        $relative = [System.IO.Path]::GetRelativePath($staging, $file.FullName)
        $expected = (Get-FileHash -LiteralPath $file.FullName).Hash
        $actual = (Get-FileHash -LiteralPath (Join-Path $appFolder $relative)).Hash
        if ($expected -ne $actual) { throw "Package file hash differs: $relative" }
    }
    Write-Host "Verified $($packagedFiles.Count) extracted files against the staged package."

    $executable = Join-Path $appFolder 'Diffault.exe'
    $normal = Start-Process -FilePath $executable -WorkingDirectory $unrelatedDirectory -PassThru
    try {
        Wait-ForDiffaultWindow $normal
        if (!$normal.CloseMainWindow() -or !$normal.WaitForExit(10000) -or $normal.ExitCode -ne 0) {
            throw 'Diffault did not close normally after showing its window.'
        }
        Write-Host 'Normal launch from the extracted ZIP and unrelated working directory passed.'
    } finally {
        if (!$normal.HasExited) { $normal.Kill() }
        $normal.Dispose()
    }

    $before = Join-Path $root 'before log.txt'
    $after = Join-Path $root 'after log.txt'
    @('Main.cs(1,2): error CS0103: missing', 'stable.go:2: undefined: stable', 'old.go:2: undefined: old') |
        Set-Content -LiteralPath $before -Encoding UTF8
    @('Main.cs(7,5): error CS0103: missing', 'Main.cs(8,5): error CS0103: missing', 'stable.go:4: undefined: stable', 'new.go:2: undefined: new') |
        Set-Content -LiteralPath $after -Encoding UTF8
    $options = @{
        FilePath = $executable
        WorkingDirectory = $unrelatedDirectory
        ArgumentList = @('--smoke-test', ('"' + $before + '"'), ('"' + $after + '"'), ('"' + $Screenshots + '"'))
        RedirectStandardError = Join-Path $root 'smoke-stderr.log'
        PassThru = $true
    }
    $smoke = Start-Process @options
    try {
        if (!$smoke.WaitForExit(45000)) { throw 'The packaged workspace smoke test timed out.' }
        if ($smoke.ExitCode -ne 0) {
            $details = Get-Content -LiteralPath $options.RedirectStandardError -Raw
            if ($details) { Write-Host $details }
            $firstLine = ($details -split '[\r\n]+' | Select-Object -First 1)
            if ($env:GITHUB_ACTIONS -eq 'true' -and $firstLine) {
                Write-Host "::error title=Diffault smoke test::$firstLine"
            }
            throw "The packaged workspace smoke test failed with exit code $($smoke.ExitCode)."
        }
    } finally {
        if (!$smoke.HasExited) { $smoke.Kill() }
        $smoke.Dispose()
    }

    if (@(Get-ChildItem -LiteralPath $Screenshots -Filter '*.png' -File).Count -lt 6) {
        throw 'The smoke test did not capture the expected workspace views.'
    }
    Write-Host 'Workspace smoke test passed on the extracted package.'
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force
}
