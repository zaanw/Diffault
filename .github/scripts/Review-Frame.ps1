param([Parameter(Mandatory)][string] $Pattern)

$frame = Get-ChildItem artifacts/workspace-screenshots -Filter $Pattern -ErrorAction SilentlyContinue | Select-Object -First 1
if (!$frame) { return }
$data = [Convert]::ToBase64String([System.IO.File]::ReadAllBytes($frame.FullName))
$chunkSize = 3200
$total = [int][Math]::Ceiling($data.Length / $chunkSize)
for ($i = 0; $i -lt $total; $i++) {
    $offset = $i * $chunkSize
    $part = $data.Substring($offset, [Math]::Min($chunkSize, $data.Length - $offset))
    Write-Host "::notice title=Windows layout review::$($frame.BaseName)~$i~$total~$part"
}
