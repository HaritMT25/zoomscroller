# Builds bin\MousePinch.exe (and with -Tests the test tools) with the C# compiler that ships with Windows (.NET Framework 4).
# A running Mouse Pinch locks its exe: it is stopped for the build and started again afterwards.
param([switch]$Tests, [switch]$NoApp)
$ErrorActionPreference = "Stop"
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
Set-Location $PSScriptRoot
New-Item -ItemType Directory -Force bin | Out-Null
$failed = 0
if ($Tests) {
    & $csc -nologo -platform:x64 -optimize -out:bin\probe.exe src\Native.cs src\Fingers.cs src\Engine.cs tests\Probe.cs; $failed += $LASTEXITCODE
    & $csc -nologo -platform:x64 -optimize -out:bin\engine_tests.exe src\Native.cs src\Fingers.cs src\Engine.cs src\Settings.cs tests\EngineTests.cs; $failed += $LASTEXITCODE
    & $csc -nologo -platform:x64 -optimize -out:bin\drive.exe src\Native.cs tests\Drive.cs; $failed += $LASTEXITCODE
}
if (-not $NoApp) {
    $exe = (Resolve-Path bin).Path + "\MousePinch.exe"
    $running = @(Get-Process MousePinch -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
    $running | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
    $src = "src\Native.cs", "src\Fingers.cs", "src\Engine.cs", "src\Settings.cs", "src\Program.cs"
    & $csc -nologo -platform:x64 -optimize -target:winexe -win32manifest:src\app.manifest -out:bin\MousePinch.exe @src; $failed += $LASTEXITCODE
    if ($running.Count) { Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe); "restarted Mouse Pinch" }
}
exit $failed
