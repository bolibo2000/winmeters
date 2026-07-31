param(
    [Parameter(Mandatory=$true)][string]$AssemblyPath
)

if (-not (Test-Path $AssemblyPath)) {
    Write-Error "Assembly file not found: $AssemblyPath"
    exit 1
}

try {
$asm = [System.Reflection.Assembly]::LoadFrom($AssemblyPath
} catch {
    Write-Error "Failed to load assembly: $_"
    exit 1
}
$names = $asm.GetManifestResourceNames()
Write-Host "Resource count: $($names.Length)"
$found = $names | Where-Object { $_ -like '*winmeters*' }
if ($found) {
    $found | ForEach-Object { Write-Host "Resource: $_" }
	exit 0
} else {
    Write-Host "No winmeters resources found."
	exit 1
}
