<#
.SYNOPSIS
  Normalizes downloaded program logos into assets/icons/apps/<catalog-id>.png.
.DESCRIPTION
  Each image is scaled to fit 160x160 (aspect ratio kept, never upscaled) and saved as PNG.
  .ico files use their largest frame. File names must match catalog software ids.
  Record the source of every logo in assets/icons/apps/SOURCES.md.
.EXAMPLE
  ./scripts/import-logos.ps1 -SourceDirectory C:\temp\logos
#>
param(
    [Parameter(Mandatory)] [string]$SourceDirectory,
    [string]$TargetDirectory,
    [int]$MaxSize = 160
)

$ErrorActionPreference = 'Stop'
if (-not $TargetDirectory) { $TargetDirectory = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..\assets\icons\apps' }
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $TargetDirectory | Out-Null

foreach ($file in Get-ChildItem $SourceDirectory -File | Where-Object { $_.Extension -in '.png', '.ico' }) {
    if ($file.Extension -eq '.ico') {
        $icon = New-Object System.Drawing.Icon $file.FullName, 256, 256
        $source = $icon.ToBitmap()
        $icon.Dispose()
    } else {
        $source = [System.Drawing.Image]::FromFile($file.FullName)
    }

    $scale = [math]::Min(1.0, [math]::Min($MaxSize / $source.Width, $MaxSize / $source.Height))
    $width = [math]::Max(1, [int][math]::Round($source.Width * $scale))
    $height = [math]::Max(1, [int][math]::Round($source.Height * $scale))

    $target = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($target)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($source, 0, 0, $width, $height)
    $g.Dispose()
    $source.Dispose()

    $out = Join-Path $TargetDirectory ($file.BaseName + '.png')
    $target.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $target.Dispose()
    "{0,-18} {1}x{2}" -f $file.BaseName, $width, $height
}
