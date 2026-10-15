# Builds bin\ZoomScroller.exe (and with -Tests the test tools) with the C# compiler that ships with Windows (.NET Framework 4).
# A running ZoomScroller locks its exe: it is stopped for the build and started again afterwards.
# -Release also packs dist\ZoomScroller-<version>.zip (app + helper extension) and dist\ZoomScroller-helper-<version>.zip
# (the extension alone, for the Chrome Web Store).
param([switch]$Tests, [switch]$NoApp, [switch]$Release)
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
    $exe = (Resolve-Path bin).Path + "\ZoomScroller.exe"
    $running = @(Get-Process ZoomScroller -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })
    $running | ForEach-Object { $_.Kill(); $_.WaitForExit(5000) | Out-Null }
    $src = "src\AssemblyInfo.cs", "src\Native.cs", "src\Fingers.cs", "src\Engine.cs", "src\Settings.cs", "src\Program.cs"
    & $csc -nologo -platform:x64 -optimize -target:winexe -win32manifest:src\app.manifest -win32icon:assets\zoomscroller.ico -out:bin\ZoomScroller.exe @src; $failed += $LASTEXITCODE
    if ($running.Count) { Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe); "restarted ZoomScroller" }
}
if ($Release -and -not $failed) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # ZipFile.CreateFromDirectory on .NET Framework writes "a\b" entry names; zip readers (and the store) expect "a/b"
    function Zip($dir, $zip) {
        $root = (Resolve-Path $dir).Path
        if (Test-Path $zip) { Remove-Item $zip }
        $z = [IO.Compression.ZipFile]::Open("$PSScriptRoot\$zip", "Create")
        try {
            Get-ChildItem $root -Recurse -File | ForEach-Object {
                $name = $_.FullName.Substring($root.Length + 1).Replace("\", "/")
                [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z, $_.FullName, $name) | Out-Null
            }
        }
        finally { $z.Dispose() }
    }
    $version = (Get-Content extension\manifest.json -Raw | ConvertFrom-Json).version
    $stage = "work\release\ZoomScroller"
    if (Test-Path work\release) { Remove-Item -Recurse -Force work\release }
    New-Item -ItemType Directory -Force $stage, dist | Out-Null
    Copy-Item bin\ZoomScroller.exe, README.md, LICENSE $stage
    Copy-Item -Recurse extension "$stage\extension"
    Zip work\release "dist\ZoomScroller-$version.zip"
    Zip extension "dist\ZoomScroller-helper-$version.zip"  # the store wants manifest.json at the zip's root
    "packed dist\ZoomScroller-$version.zip and dist\ZoomScroller-helper-$version.zip"
}
exit $failed
