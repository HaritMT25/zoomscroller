# Renders the store images with headless Chrome: screenshot.html -> screenshot-1280x800.png, promo.html -> promo-440x280.png.
# The store wants 24-bit PNGs (no alpha channel), so Chrome's output is redrawn onto an opaque bitmap.
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
Set-Location $PSScriptRoot
$chrome = @("$env:ProgramFiles\Google\Chrome\Application\chrome.exe", "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
            "$env:LOCALAPPDATA\Google\Chrome\Application\chrome.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $chrome) { throw "Chrome not found" }
$profile = Join-Path $env:TEMP "zoomscroller-render"
foreach ($job in @(@("screenshot.html", "screenshot-1280x800.png", 1280, 800), @("promo.html", "promo-440x280.png", 440, 280))) {
    $html, $png, $w, $h = $job
    $raw = Join-Path $env:TEMP "zoomscroller-$png"
    $url = "file:///" + ((Resolve-Path $html).Path -replace "\\", "/")
    # Chrome reports "bytes written" on stderr, which Windows PowerShell turns into an error under "Stop"
    $ErrorActionPreference = "Continue"
    & $chrome --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --user-data-dir="$profile" `
        --allow-file-access-from-files "--window-size=$w,$h" "--screenshot=$raw" $url 2>&1 | Out-Null
    $ErrorActionPreference = "Stop"
    for ($i = 0; $i -lt 50 -and -not (Test-Path $raw); $i++) { Start-Sleep -Milliseconds 200 }
    $src = [System.Drawing.Image]::FromFile($raw)
    $out = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $g = [System.Drawing.Graphics]::FromImage($out)
    $g.Clear([System.Drawing.Color]::White)
    $g.DrawImage($src, 0, 0, $w, $h)
    $g.Dispose(); $src.Dispose()
    $out.Save((Join-Path $PSScriptRoot $png), [System.Drawing.Imaging.ImageFormat]::Png)
    $out.Dispose()
    Remove-Item $raw
    "wrote store\$png"
}
Remove-Item -Recurse -Force $profile -ErrorAction SilentlyContinue
