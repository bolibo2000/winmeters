$cat = New-Object System.Diagnostics.PerformanceCounterCategory("Network Interface")
$instances = $cat.GetInstanceNames()
Write-Host "=== Network Interface PerformanceCounter instances ==="
foreach ($inst in $instances) {
    Write-Host "  '$inst'"
    
    # Test reading Bytes Received/sec
    $ctr = New-Object System.Diagnostics.PerformanceCounter("Network Interface", "Bytes Received/sec", $inst, $true)
    $null = $ctr.NextValue()
}

Write-Host ""
Write-Host "Waiting 2 seconds..."
Start-Sleep -Seconds 2

Write-Host ""
Write-Host "=== Speed readings ==="
foreach ($inst in $instances) {
    $recv = New-Object System.Diagnostics.PerformanceCounter("Network Interface", "Bytes Received/sec", $inst, $true)
    $sent = New-Object System.Diagnostics.PerformanceCounter("Network Interface", "Bytes Sent/sec", $inst, $true)
    $rVal = $recv.NextValue()
    $sVal = $sent.NextValue()
    Write-Host ("  {0,-55} Down={1,10:F1} KB/s  Up={2,10:F1} KB/s" -f $inst, ($rVal/1024), ($sVal/1024))
}
