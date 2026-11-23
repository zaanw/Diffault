param([Parameter(Mandatory)][string] $Pattern)
$frame = Get-ChildItem artifacts/workspace-screenshots -Filter $Pattern | Select-Object -First 1
if (!$frame) { return }
$data = [Convert]::ToBase64String([IO.File]::ReadAllBytes($frame.FullName))
$size = 3200
$total = [Math]::Ceiling($data.Length / $size)
for ($i = 0; $i -lt $total; $i++) {
    $part = $data.Substring($i * $size, [Math]::Min($size, $data.Length - $i * $size))
    Write-Host "::notice title=Beta native preview::$($frame.BaseName)~$i~$total~$part"
}
