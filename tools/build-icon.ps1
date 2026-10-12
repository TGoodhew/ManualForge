<#
.SYNOPSIS
    Builds src\ManualForge.App\Assets\AppIcon.ico from the drawings in art\icon (#67).

.DESCRIPTION
    Each size is rendered from SVG at that size, never scaled down from a larger picture: 16, 20, 24
    and 32 px from manualforge-small.svg, which is drawn to fall on whole pixels at 16, and 40, 48,
    64 and 256 px from manualforge.svg. Microsoft Edge, headless, does the rendering, on a
    transparent background. The PNGs go to artifacts\icon, and the .ico holds them all as PNG
    frames, as Windows has read them since Vista.

    The one .ico is the app's window and title-bar icon, the icon built into ManualForge.App.exe
    (the Start menu, the taskbar, Alt+Tab and Settings > Apps), and the installer's.

.EXAMPLE
    ./tools/build-icon.ps1
#>
[CmdletBinding()]
param(
    [string] $Edge = (@("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1)
)

$ErrorActionPreference = 'Stop'
if (-not $Edge) { throw 'Microsoft Edge was not found; pass -Edge.' }
Add-Type -AssemblyName System.Drawing

$repo = Split-Path $PSScriptRoot -Parent
$art = Join-Path $repo 'art\icon'
$out = Join-Path $repo 'artifacts\icon'
$ico = Join-Path $repo 'src\ManualForge.App\Assets\AppIcon.ico'
New-Item -ItemType Directory -Force $out | Out-Null

$frames = foreach ($size in 16, 20, 24, 32, 40, 48, 64, 256) {
    $svg = Join-Path $art $(if ($size -le 32) { 'manualforge-small.svg' } else { 'manualforge.svg' })
    $page = Join-Path $out "render-$size.html"
    $shot = Join-Path $out "shot-$size.png"
    $png = Join-Path $out "manualforge-$size.png"
    $uri = 'file:///' + ($svg -replace '\\', '/')
    "<!doctype html><html style='background:transparent'><body style='margin:0;background:transparent'><img src='$uri' width='$size' height='$size' style='display:block'></body></html>" |
        Set-Content -LiteralPath $page -Encoding UTF8
    if (Test-Path $shot) { Remove-Item -LiteralPath $shot }
    & $Edge --headless=new --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --default-background-color=00000000 `
        --window-size=400,400 "--screenshot=$shot" ('file:///' + ($page -replace '\\', '/')) 2>&1 | Out-Null
    for ($i = 0; $i -lt 50 -and -not (Test-Path $shot); $i++) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $shot)) { throw "Edge did not render $size px." }

    # The picture sits in the top-left corner of the screenshot; keep exactly its pixels.
    $whole = [System.Drawing.Bitmap]::FromFile($shot)
    try {
        $frame = $whole.Clone([System.Drawing.Rectangle]::new(0, 0, $size, $size), [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $frame.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
        $frame.Dispose()
    }
    finally { $whole.Dispose() }
    Remove-Item -LiteralPath $page, $shot
    [pscustomobject]@{ Size = $size; Bytes = [IO.File]::ReadAllBytes($png) }
}

# ICONDIR, one ICONDIRENTRY per frame, then the PNGs.
$stream = New-Object IO.MemoryStream
$writer = New-Object IO.BinaryWriter $stream
$writer.Write([uint16] 0); $writer.Write([uint16] 1); $writer.Write([uint16] $frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $edge = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $writer.Write([byte] $edge); $writer.Write([byte] $edge); $writer.Write([byte] 0); $writer.Write([byte] 0)
    $writer.Write([uint16] 1); $writer.Write([uint16] 32)
    $writer.Write([uint32] $f.Bytes.Length); $writer.Write([uint32] $offset)
    $offset += $f.Bytes.Length
}
foreach ($f in $frames) { $writer.Write($f.Bytes) }
$writer.Flush()
[IO.File]::WriteAllBytes($ico, $stream.ToArray())
$writer.Dispose()

Write-Host ("{0}: {1} sizes ({2}), {3:N0} KB" -f $ico, $frames.Count, (($frames | ForEach-Object Size) -join ', '), ((Get-Item $ico).Length / 1KB))
Write-Host "PNGs in $out"
