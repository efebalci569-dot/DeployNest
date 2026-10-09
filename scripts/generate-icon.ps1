<#
.SYNOPSIS
  Regenerates the DeployNest logo assets (ICO for the EXE, PNG and SVG for docs/website).
.DESCRIPTION
  The logo is a rounded tile with a blue-to-cyan gradient, a soft top highlight, a download
  arrow and a two-layer "nest" underneath. The same geometry (64x64 units) is used by
  assets/images/logo.svg and the XAML logo in src/DeployNest.App/Themes/Controls.xaml.
  Run with Windows PowerShell 5.1 or PowerShell 7 on Windows.
#>
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\assets' }
Add-Type -AssemblyName System.Drawing

$iconDir = Join-Path $OutputDirectory 'icons'
$imageDir = Join-Path $OutputDirectory 'images'
New-Item -ItemType Directory -Force $iconDir, $imageDir | Out-Null

function P([float]$x, [float]$y, [float]$k) { New-Object System.Drawing.PointF ($x * $k), ($y * $k) }

function New-RoundedPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($x, $y, $d, $d, 180, 90)
    $path.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $path.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $path.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-LogoBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 64.0

    # Tile with diagonal gradient.
    $tile = New-RoundedPath 0 0 $size $size (15 * $k)
    $gradient = New-Object System.Drawing.Drawing2D.LinearGradientBrush((P 0 0 $k), (P 64 64 $k),
        [System.Drawing.ColorTranslator]::FromHtml('#3B5BFD'), [System.Drawing.ColorTranslator]::FromHtml('#0FC3D9'))
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend 3
    $blend.Colors = @([System.Drawing.ColorTranslator]::FromHtml('#3B5BFD'), [System.Drawing.ColorTranslator]::FromHtml('#2A86F2'), [System.Drawing.ColorTranslator]::FromHtml('#0FC3D9'))
    $blend.Positions = @(0.0, 0.5, 1.0)
    $gradient.InterpolationColors = $blend
    $g.FillPath($gradient, $tile)

    # Soft highlight in the upper-left corner.
    if ($size -ge 24) {
        $g.SetClip($tile)
        $glow = New-Object System.Drawing.Drawing2D.GraphicsPath
        $glow.AddEllipse(-18 * $k, -26 * $k, 70 * $k, 56 * $k)
        $glowBrush = New-Object System.Drawing.Drawing2D.PathGradientBrush $glow
        $glowBrush.CenterColor = [System.Drawing.Color]::FromArgb(70, 255, 255, 255)
        $glowBrush.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 255, 255, 255))
        $g.FillPath($glowBrush, $glow)
        $g.ResetClip()
        $glowBrush.Dispose(); $glow.Dispose()
    }

    $round = [System.Drawing.Drawing2D.LineCap]::Round
    function New-Pen([int]$alpha, [float]$width) {
        $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb($alpha, 255, 255, 255)), ([Math]::Max(1.4, $width))
        $pen.StartCap = $round; $pen.EndCap = $round
        $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
        return $pen
    }

    # Inner (back) layer of the nest.
    if ($size -ge 32) {
        $inner = New-Pen 120 (3.4 * $k)
        $g.DrawBezier($inner, (P 21 40.5 $k), (P 24 46 $k), (P 40 46 $k), (P 43 40.5 $k))
        $inner.Dispose()
    }

    # Arrow and outer nest.
    $pen = New-Pen 255 (5.2 * $k)
    $g.DrawLine($pen, (P 32 12.5 $k), (P 32 33 $k))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((P 23.5 24.5 $k), (P 32 33 $k), (P 40.5 24.5 $k)))
    $g.DrawBezier($pen, (P 13.5 37 $k), (P 16 51.5 $k), (P 48 51.5 $k), (P 50.5 37 $k))
    $pen.Dispose()

    $gradient.Dispose(); $tile.Dispose(); $g.Dispose()
    return $bmp
}

# ICO with PNG-compressed entries (supported since Windows Vista).
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($size in $sizes) {
    $bmp = New-LogoBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

$icoPath = Join-Path $iconDir 'deploynest.ico'
$fs = [System.IO.File]::Create($icoPath)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Dispose(); $fs.Dispose()

$large = New-LogoBitmap 512
$large.Save((Join-Path $iconDir 'deploynest-512.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$large.Dispose()

$svg = @'
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 64 64" role="img" aria-label="DeployNest">
  <defs>
    <linearGradient id="dn-bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#3B5BFD"/>
      <stop offset=".5" stop-color="#2A86F2"/>
      <stop offset="1" stop-color="#0FC3D9"/>
    </linearGradient>
    <radialGradient id="dn-glow" cx="17" cy="2" r="36" gradientUnits="userSpaceOnUse">
      <stop offset="0" stop-color="#fff" stop-opacity=".28"/>
      <stop offset="1" stop-color="#fff" stop-opacity="0"/>
    </radialGradient>
  </defs>
  <rect width="64" height="64" rx="15" fill="url(#dn-bg)"/>
  <rect width="64" height="64" rx="15" fill="url(#dn-glow)"/>
  <path d="M21 40.5C24 46 40 46 43 40.5" fill="none" stroke="#fff" stroke-opacity=".47" stroke-width="3.4" stroke-linecap="round"/>
  <g fill="none" stroke="#fff" stroke-width="5.2" stroke-linecap="round" stroke-linejoin="round">
    <path d="M32 12.5V33"/>
    <path d="M23.5 24.5L32 33l8.5-8.5"/>
    <path d="M13.5 37C16 51.5 48 51.5 50.5 37"/>
  </g>
</svg>
'@
Set-Content -Path (Join-Path $imageDir 'logo.svg') -Value $svg -Encoding UTF8

Write-Host "Logo assets generated: $iconDir, $imageDir"
