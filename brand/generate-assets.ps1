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

    There is no tile SVG to edit. A tile is composed here from layers: ripcord-ground.svg (the material,
    full-bleed, no shape) and a mark, with the shape and the edge added by this script. That split exists
    for the platforms still to come. Android masks the icon to a shape of the launcher's choosing and
    macOS 26 draws its own edge, so neither may be handed a shape or a border — only the layers. Windows
    draws neither, so the Windows target adds both. See brand/README.md, "The tile is layers".

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
        [string]$Svg,
        [string]$Markup,
        [Parameter(Mandatory)][int]$CanvasWidth,
        [Parameter(Mandatory)][int]$CanvasHeight,
        [Parameter(Mandatory)][int]$ArtSize,
        [Parameter(Mandatory)][string]$Out,
        [string]$Color = '#000000',
        # A wide source (the lockup) is sized by width alone and keeps its own aspect ratio.
        [switch]$Wide
    )

    $markup = if ($Markup) { $Markup } else { Get-Content -Path (Join-Path $brandDir $Svg) -Raw }
    $artHeight = if ($Wide) { 'auto' } else { "${ArtSize}px" }
    $stem = [IO.Path]::GetFileNameWithoutExtension($Out)
    $page = Join-Path $work "$stem-$CanvasWidth`x$CanvasHeight.html"

    $html = @"
<!doctype html>
<html><head><meta charset="utf-8"><style>
  html, body { margin: 0; padding: 0; background: transparent; }
  body { width: ${CanvasWidth}px; height: ${CanvasHeight}px; display: grid; place-items: center; color: $Color; }
  body > svg { display: block; width: ${ArtSize}px; height: $artHeight; }
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
    The contents of an SVG file's root element, so layers can be stacked inside one tile. Every layer is
    drawn in the same 64-unit box, which is what lets them stack without any transform.
#>
function Get-SvgBody {
    param([Parameter(Mandatory)][string]$Svg)

    $markup = Get-Content -Path (Join-Path $brandDir $Svg) -Raw
    if ($markup -notmatch '(?s)<svg[^>]*>(.*)</svg>') { throw "no <svg> element in $Svg" }
    return $Matches[1]
}

<#
    A tile: the ground clipped to a rounded square, the mark over it, and optionally the edge.

    The edge is the finish: a top highlight and a hairline, the same material the console cards carry. It
    is optional because it belongs to the platform, not to the mark. The small cut goes without it — a
    sub-pixel stroke at 16 px is mud, not an edge.

    -MarkTransform places the master mark at 72% of the tile, centred, inside the ø58 circle that
    Android's adaptive crop leaves alone. The small cut is already drawn in tile coordinates.
#>
function New-TileSvg {
    param(
        [Parameter(Mandatory)][double]$Radius,
        [Parameter(Mandatory)][string]$Mark,
        [string]$MarkTransform = '',
        [switch]$Finish
    )

    $ground = Get-SvgBody 'ripcord-ground.svg'
    $markBody = Get-SvgBody $Mark
    $inset = 0.375
    $edge = ''
    if ($Finish) {
        $edge = @"
  <rect width="64" height="64" rx="$Radius" fill="url(#finish-highlight)" />
  <rect x="$inset" y="$inset" width="$(64 - 2 * $inset)" height="$(64 - 2 * $inset)" rx="$($Radius - $inset)" fill="none" stroke="#FFFFFF" stroke-opacity="0.14" stroke-width="$(2 * $inset)" />
"@
    }

    return @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" width="64" height="64">
  <defs>
    <clipPath id="tile-shape"><rect width="64" height="64" rx="$Radius" /></clipPath>
    <linearGradient id="finish-highlight" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.16" />
      <stop offset="0.5" stop-color="#FFFFFF" stop-opacity="0" />
    </linearGradient>
  </defs>
  <g clip-path="url(#tile-shape)">$ground</g>
$edge  <g transform="$MarkTransform">$markBody</g>
</svg>
"@
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

# ---- The Windows tiles, composed from layers. Radius 15 is 23% of the tile; the small cut's 12 is on its grid. ----
$tiles = @{
    Master = New-TileSvg -Radius 15 -Mark 'ripcord-mark-ondark.svg' `
        -MarkTransform 'translate(32,32) scale(0.72) translate(-30.5,-32)' -Finish
    Small = New-TileSvg -Radius 12 -Mark 'ripcord-mark-small.svg'
}

# ---- MSIX / packaging assets. Square art is full-bleed tile; the wide and splash canvases centre it. ----
$square = @(
    @{ Name = 'Square44x44Logo.scale-200.png'; Size = 88; Tile = 'Master' }
    @{ Name = 'Square44x44Logo.targetsize-24_altform-unplated.png'; Size = 24; Tile = 'Small' }
    @{ Name = 'Square44x44Logo.targetsize-48_altform-lightunplated.png'; Size = 48; Tile = 'Master' }
    @{ Name = 'Square150x150Logo.scale-200.png'; Size = 300; Tile = 'Master' }
    @{ Name = 'StoreLogo.png'; Size = 50; Tile = 'Master' }
)

foreach ($asset in $square) {
    $out = Join-Path $assetsDir $asset.Name
    Convert-SvgToPng -Markup $tiles[$asset.Tile] -CanvasWidth $asset.Size -CanvasHeight $asset.Size `
        -ArtSize $asset.Size -Out $out
    Write-Host "  $($asset.Name)  $($asset.Size)x$($asset.Size)"
}

# The wide tile and the splash are the lockup on a transparent field rather than a stretched tile: Windows
# draws them against a colour of its own, and a second rounded rectangle inside that reads as a sticker.
# Both use the white-word cut. The wide tile sits on the tile colour, which is the accent for a transparent
# BackgroundColor; the splash sits on the SplashScreen BackgroundColor the manifest pins to the ground's
# dark end. Either way the ground behind the word is dark, and neither follows the light/dark theme.
# The width leaves at least one blue dash of clear space on every side.
Convert-SvgToPng -Svg 'ripcord-lockup-ondark.svg' -CanvasWidth 620 -CanvasHeight 300 -ArtSize 440 -Wide `
    -Out (Join-Path $assetsDir 'Wide310x150Logo.scale-200.png')
Write-Host '  Wide310x150Logo.scale-200.png  620x300 (lockup)'

Convert-SvgToPng -Svg 'ripcord-lockup-ondark.svg' -CanvasWidth 1240 -CanvasHeight 600 -ArtSize 640 -Wide `
    -Out (Join-Path $assetsDir 'SplashScreen.scale-200.png')
Write-Host '  SplashScreen.scale-200.png  1240x600 (lockup)'

# The lock screen badge must be white on transparent — no tile, no colour.
Convert-SvgToPng -Svg 'ripcord-mono.svg' -CanvasWidth 48 -CanvasHeight 48 -ArtSize 48 -Color '#FFFFFF' `
    -Out (Join-Path $assetsDir 'LockScreenLogo.scale-200.png')
Write-Host '  LockScreenLogo.scale-200.png  48x48 (mono)'

# ---- AppIcon.ico ----
$icoSizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($size in $icoSizes) {
    $tile = if ($size -le 32) { $tiles.Small } else { $tiles.Master }
    $frame = Join-Path $work "ico-$size.png"
    Convert-SvgToPng -Markup $tile -CanvasWidth $size -CanvasHeight $size -ArtSize $size -Out $frame
    $frame
}

New-IcoFile -Frames $frames -Out (Join-Path $assetsDir 'AppIcon.ico')
Write-Host "  AppIcon.ico  $($icoSizes -join ', ')"

Remove-Item -Recurse -Force $work
Write-Host "Done. Assets written to $assetsDir"
