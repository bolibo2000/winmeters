# Get ISensor and HardwareType/SensorType enums
$dllPath = "v:\Work_Folder\WinMeters-v2\LibreHardwareMonitorLib.v0.9.4.dll"
$assembly = [System.Reflection.Assembly]::LoadFrom($dllPath)

# Use metadata loading context for better type inspection
try {
    $allTypes = $assembly.GetTypes()
}
catch [System.Reflection.ReflectionTypeLoadException] {
    $ex = $_.Exception
    $allTypes = $ex.Types | Where-Object { $_ -ne $null }
    Write-Host "Loader Exceptions:" -ForegroundColor Red
    foreach ($le in ($ex.LoaderExceptions | Select-Object -First 5)) {
        Write-Host "  $($le.Message)" -ForegroundColor Red
    }
}

# Try to find ISensor
Write-Host ""
Write-Host "=== Looking for ISensor ===" -ForegroundColor Cyan
$isensor = $allTypes | Where-Object { $_.Name -eq "ISensor" }
if ($isensor) {
    Write-Host "Found: $($isensor.FullName)"
    Write-Host "  Properties:"
    foreach ($prop in $isensor.GetProperties()) {
        $propTypeName = $prop.PropertyType.Name
        Write-Host "    $propTypeName $($prop.Name)"
    }
    Write-Host "  Methods:"
    foreach ($method in ($isensor.GetMethods() | Where-Object { -not $_.IsSpecialName })) {
        Write-Host "    $($method.ReturnType.Name) $($method.Name)()"
    }
}
else {
    Write-Host "ISensor not found in loaded types" -ForegroundColor Yellow
}

# Try to find SensorType enum
Write-Host ""
Write-Host "=== Looking for SensorType ===" -ForegroundColor Cyan
$sensorType = $allTypes | Where-Object { $_.Name -eq "SensorType" }
if ($sensorType -and $sensorType.IsEnum) {
    Write-Host "Found: $($sensorType.FullName)"
    foreach ($val in [System.Enum]::GetNames($sensorType)) {
        Write-Host "    $val"
    }
}
else {
    Write-Host "SensorType not found" -ForegroundColor Yellow
}

# Try to find HardwareType enum  
Write-Host ""
Write-Host "=== Looking for HardwareType ===" -ForegroundColor Cyan
$hwType = $allTypes | Where-Object { $_.Name -eq "HardwareType" }
if ($hwType -and $hwType.IsEnum) {
    Write-Host "Found: $($hwType.FullName)"
    foreach ($val in [System.Enum]::GetNames($hwType)) {
        Write-Host "    $val"
    }
}
else {
    Write-Host "HardwareType not found" -ForegroundColor Yellow
}

# Try to find Computer class
Write-Host ""
Write-Host "=== Looking for Computer class ===" -ForegroundColor Cyan
$computer = $allTypes | Where-Object { $_.Name -eq "Computer" -and $_.IsClass }
if ($computer) {
    Write-Host "Found: $($computer.FullName)"
    Write-Host "  Implements: $($computer.GetInterfaces() | ForEach-Object { $_.Name })"
    Write-Host "  Constructors:"
    foreach ($ctor in $computer.GetConstructors()) {
        $params = ($ctor.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
        Write-Host "    new Computer($params)"
    }
    Write-Host "  Public Properties:"
    foreach ($prop in $computer.GetProperties()) {
        Write-Host "    $($prop.PropertyType.Name) $($prop.Name)"
    }
}
else {
    Write-Host "Computer class not found" -ForegroundColor Yellow
}

# List ALL types that were loaded
Write-Host ""
Write-Host "=== All Loaded Types Count: $($allTypes.Count) ===" -ForegroundColor Cyan
$allTypes | ForEach-Object { Write-Host $_.FullName }
