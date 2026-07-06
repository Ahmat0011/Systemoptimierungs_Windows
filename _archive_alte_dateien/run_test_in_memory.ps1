try {
    Write-Host "Loading SystemOptimierer.dll..."
    $optPath = "D:\sahma\Documents\GitHub\Systemoptimierungs_Windows\bin\Debug\net10.0-windows\SystemOptimierer.dll"
    $optBytes = [System.IO.File]::ReadAllBytes($optPath)
    $optAssembly = [System.Reflection.Assembly]::Load($optBytes)
    Write-Host "Loaded SystemOptimierer: $($optAssembly.FullName)"
    
    Write-Host "Loading Tests.dll..."
    $testPath = "D:\sahma\Documents\GitHub\Systemoptimierungs_Windows\Tests\bin\Debug\net10.0-windows\Tests.dll"
    $testBytes = [System.IO.File]::ReadAllBytes($testPath)
    $testAssembly = [System.Reflection.Assembly]::Load($testBytes)
    Write-Host "Loaded Tests: $($testAssembly.FullName)"
    
    try {
        Write-Host "Calling GetTypes..."
        $types = $testAssembly.GetTypes()
        Write-Host "Loaded $($types.Count) types."
    } catch [System.Reflection.ReflectionTypeLoadException] {
        Write-Host "ReflectionTypeLoadException caught!"
        foreach ($le in $_.Exception.LoaderExceptions) {
            Write-Host "LoaderException: $($le.Message)"
        }
    }
} catch {
    Write-Error $_
}
