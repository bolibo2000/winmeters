$bytes = [System.IO.File]::ReadAllBytes('winmeters.ico')
$reserved = [BitConverter]::ToUInt16($bytes, 0)
$type = [BitConverter]::ToUInt16($bytes, 2)
if ($reserved -ne 0 -or $type -ne 1) {
    throw "Invalid ICO file header"
}
$count = [BitConverter]::ToUInt16($bytes, 4)
Write-Host "Entries: $count"
for ($i = 0; $i -lt $count; $i++) {
    $off = 6 + ($i * 16)
    $w = $bytes[$off]
    $h = $bytes[$off + 1]
    $len = [BitConverter]::ToUInt32($bytes, $off + 8)
    $width = if ($w -eq 0) { 256 } else { $w }
    $height = if ($h -eq 0) { 256 } else { $h }
    Write-Host "Entry $i : ${width}x${height} - $len bytes"
}
