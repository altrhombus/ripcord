<#
.SYNOPSIS
    Renders every app icon Ripcord ships from the SVG sources in this folder.

.DESCRIPTION
    One source of truth: edit the SVGs here, run this, commit the result. Nothing under
    src/Ripcord.App/Assets/ should ever be edited by hand.

    Rasterising is done by headless Edge, which is on every Windows box that can build this project, so
    there is no dependency on Inkscape, ImageMagick or a Node toolchain. The .ico is assembled here rather
    than by a tool because the format is trivial: a header, one directory entry per size, then PNG blobs
    (Vista and later accept PNG-compressed frames directly).

    Sizes at or below 32 px come from the small cut, which is a separate drawing with pixel-aligned edges —
    scaling the master down produces a soft, muddy icon at exactly the size most users see most often.

.PARAMETER Edge
    Path to msedge.exe, if it is somewhere unusual.
#>
[CmdletBinding()]
param(
    [string]$Edge
)

$ErrorActionPreference = 'Stop'

$brandDir = $PSScriptRoot
$assetsDir = Join-Path (Split-Path $brandDir -Parent) 'src\Ripcord.App\Assets'
$work = Join-Path ([IO.Path]::GetTempPath()) ("ripcord-brand-" + [Guid]::NewGuid().ToString('n'))

if (-not (Test-Path $assetsDir)) { throw "asset folder not found: $assetsDir" }
New-Item -ItemType Directory -Path $work -Force | Out-Null

function Resolve-Edge {
    param([string]$Explicit)

    if ($Explicit) {
        if (-not (Test-Path $Explicit)) { throw "msedge.exe not found at $Explicit" }
        return $Explicit
    }

    $candidates = @(
        "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
        "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe"
    )

    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) { return $candidate }
    }

    throw 'msedge.exe not found. Pass -Edge <path>.'
}

<#
    Render one SVG onto a transparent canvas. The SVG is inlined into a throwaway page rather than loaded
    directly so the canvas can be a different shape from the art (wide tiles, splash screens) and so
    currentColor can be set from outside.
#>
function Convert-SvgToPng {
    param(
        [Parameter(Mandatory)][string]$Svg,
        [Parameter(Mandatory)][int]$CanvasWidth,
        [Parameter(Mandatory)][int]$CanvasHeight,
        [Parameter(Mandatory)][int]$ArtSize,
        [Parameter(Mandatory)][string]$Out,
        [string]$Color = '#000000'
    )

    $markup = Get-Content -Path (Join-Path $brandDir $Svg) -Raw
    $stem = [IO.Path]::GetFileNameWithoutExtension($Out)
    $page = Join-Path $work "$stem-$CanvasWidth`x$CanvasHeight.html"

    $html = @"
<!doctype html>
<html><head><meta charset="utf-8"><style>
  html, body { margin: 0; padding: 0; background: transparent; }
  body { width: ${CanvasWidth}px; height: ${CanvasHeight}px; display: grid; place-items: center; color: $Color; }
  svg { display: block; width: ${ArtSize}px; height: ${ArtSize}px; }
</style></head><body>
$markup
</body></html>
"@

    Set-Content -Path $page -Value $html -Encoding UTF8

    $arguments = @(
        '--headless=new'
        '--disable-gpu'
        '--hide-scrollbars'
        '--force-device-scale-factor=1'
        '--default-background-color=00000000'
        "--window-size=$CanvasWidth,$CanvasHeight"
        "--screenshot=$Out"
        ('file:///' + $page.Replace('\', '/'))
    )

    & $edgeExe @arguments 2>$null | Out-Null

    if (-not (Test-Path $Out)) { throw "render failed: $Out" }
}

<#
    Assemble an .ico from already-rendered PNG frames. ICONDIR (6 bytes), then a 16-byte ICONDIRENTRY per
    frame, then the PNG payloads. A width or height of 256 is stored as 0.
#>
function New-IcoFile {
    param(
        [Parameter(Mandatory)][string[]]$Frames,
        [Parameter(Mandatory)][string]$Out
    )

    $blobs = $Frames | ForEach-Object { , [IO.File]::ReadAllBytes($_) }
    $sizes = $Frames | ForEach-Object { [int]([IO.Path]::GetFileNameWithoutExtension($_) -replace '\D') }

    $stream = [IO.File]::Create($Out)
    try {
        $writer = New-Object IO.BinaryWriter($stream)

        $writer.Write([UInt16]0)                 # reserved
        $writer.Write([UInt16]1)                 # type: icon
        $writer.Write([UInt16]$blobs.Count)

        $offset = 6 + (16 * $blobs.Count)
        for ($i = 0; $i -lt $blobs.Count; $i++) {
            $dimension = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension)      # width
            $writer.Write([byte]$dimension)      # height
            $writer.Write([byte]0)               # palette size: none
            $writer.Write([byte]0)               # reserved
            $writer.Write([UInt16]1)             # colour planes
            $writer.Write([UInt16]32)            # bits per pixel
            $writer.Write([UInt32]$blobs[$i].Length)
            $writer.Write([UInt32]$offset)
            $offset += $blobs[$i].Length
        }

        foreach ($blob in $blobs) { $writer.Write($blob) }
        $writer.Flush()
    }
    finally {
        $stream.Dispose()
    }
}

$edgeExe = Resolve-Edge -Explicit $Edge
Write-Host "Rendering with $edgeExe"

# ---- MSIX / packaging assets. Square art is full-bleed tile; the wide and splash canvases centre it. ----
$square = @(
    @{ Name = 'Square44x44Logo.scale-200.png'; Size = 88; Source = 'ripcord-tile.svg' }
    @{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png'; Size = 24; Source = 'ripcord-tile-small.svg' }
    @{ Name = 'Square44x44Logo.targetsize-48_altform-lightunplated.png'; Size = 48; Source = 'ripcord-tile.svg' }
    @{ Name = 'Square150x150Logo.scale-200.png'; Size = 300; Source = 'ripcord-tile.svg' }
    @{ Name = 'StoreLogo.png'; Size = 50; Source = 'ripcord-tile.svg' }
)

foreach ($asset in $square) {
    $out = Join-Path $assetsDir $asset.Name
    Convert-SvgToPng -Svg $asset.Source -CanvasWidth $asset.Size -CanvasHeight $asset.Size `
        -ArtSize $asset.Size -Out $out
    Write-Host "  $($asset.Name)  $($asset.Size)x$($asset.Size)"
}

# The wide tile is the mark on a transparent field rather than a stretched tile: Windows draws it against
# the app's own tile colour, and a second rounded rectangle inside that reads as a sticker.
Convert-SvgToPng -Svg 'ripcord-tile.svg' -CanvasWidth 620 -CanvasHeight 300 -ArtSize 240 `
    -Out (Join-Path $assetsDir 'Wide310x150Logo.scale-200.png')
Write-Host '  Wide310x150Logo.scale-200.png  620x300'

Convert-SvgToPng -Svg 'ripcord-tile.svg' -CanvasWidth 1240 -CanvasHeight 600 -ArtSize 300 `
    -Out (Join-Path $assetsDir 'SplashScreen.scale-200.png')
Write-Host '  SplashScreen.scale-200.png  1240x600'

# The lock screen badge must be white on transparent — no tile, no colour.
Convert-SvgToPng -Svg 'ripcord-mono.svg' -CanvasWidth 48 -CanvasHeight 48 -ArtSize 48 -Color '#FFFFFF' `
    -Out (Join-Path $assetsDir 'LockScreenLogo.scale-200.png')
Write-Host '  LockScreenLogo.scale-200.png  48x48 (mono)'

# ---- AppIcon.ico ----
$icoSizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($size in $icoSizes) {
    $source = if ($size -le 32) { 'ripcord-tile-small.svg' } else { 'ripcord-tile.svg' }
    $frame = Join-Path $work "ico-$size.png"
    Convert-SvgToPng -Svg $source -CanvasWidth $size -CanvasHeight $size -ArtSize $size -Out $frame
    $frame
}

New-IcoFile -Frames $frames -Out (Join-Path $assetsDir 'AppIcon.ico')
Write-Host "  AppIcon.ico  $($icoSizes -join ', ')"

Remove-Item -Recurse -Force $work
Write-Host "Done. Assets written to $assetsDir"
