<#
.SYNOPSIS
  Regenerates the DeployNest logo assets (ICO for the EXE, PNG and SVG for docs/website).
.DESCRIPTION
  The logo is drawn from the same geometry as assets/images/logo.svg so all formats stay
  identical. Run from the repository root with Windows PowerShell 5.1 or PowerShell 7 on Windows.
#>
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$iconDir = Join-Path $OutputDirectory 'icons'
$imageDir = Join-Path $OutputDirectory 'images'
New-Item -ItemType Directory -Force $iconDir, $imageDir | Out-Null

function New-LogoBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $s = [float]$size
    $r = $s * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r * 2, $r * 2, 180, 90)
    $path.AddArc($s - $r * 2, 0, $r * 2, $r * 2, 270, 90)
    $path.AddArc($s - $r * 2, $s - $r * 2, $r * 2, $r * 2, 0, 90)
    $path.AddArc(0, $s - $r * 2, $r * 2, $r * 2, 90, 90)
    $path.CloseFigure()

    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF 0, 0),
        (New-Object System.Drawing.PointF $s, $s),
        [System.Drawing.ColorTranslator]::FromHtml('#2F6FED'),
        [System.Drawing.ColorTranslator]::FromHtml('#12B5D6'))
    $g.FillPath($brush, $path)

    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([Math]::Max(1.6, $s * 0.085))
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $g.DrawLine($pen, $s * 0.5, $s * 0.20, $s * 0.5, $s * 0.52)
    $g.DrawLines($pen, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($s * 0.36), ($s * 0.40)),
        (New-Object System.Drawing.PointF ($s * 0.50), ($s * 0.54)),
        (New-Object System.Drawing.PointF ($s * 0.64), ($s * 0.40))))
    $g.DrawBezier($pen,
        (New-Object System.Drawing.PointF ($s * 0.22), ($s * 0.60)),
        (New-Object System.Drawing.PointF ($s * 0.27), ($s * 0.80)),
        (New-Object System.Drawing.PointF ($s * 0.73), ($s * 0.80)),
        (New-Object System.Drawing.PointF ($s * 0.78), ($s * 0.60)))

    $pen.Dispose(); $brush.Dispose(); $path.Dispose(); $g.Dispose()
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
    <linearGradient id="dn-g" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#2F6FED"/>
      <stop offset="1" stop-color="#12B5D6"/>
    </linearGradient>
  </defs>
  <rect width="64" height="64" rx="14" fill="url(#dn-g)"/>
  <g fill="none" stroke="#fff" stroke-width="5.4" stroke-linecap="round" stroke-linejoin="round">
    <path d="M32 12.8V33.3"/>
    <path d="M23.04 25.6L32 34.56L40.96 25.6"/>
    <path d="M14.08 38.4C17.28 51.2 46.72 51.2 49.92 38.4"/>
  </g>
</svg>
'@
Set-Content -Path (Join-Path $imageDir 'logo.svg') -Value $svg -Encoding UTF8

Write-Host "Logo assets generated: $iconDir, $imageDir"
