<#
.SYNOPSIS
  Copies the catalog, program logos, app logo and screenshots into website/ so the static site
  (deployed by Vercel from the website/ folder) always shows the same data as the app.
.EXAMPLE
  ./scripts/build-website.ps1
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$site = Join-Path $root 'website'

$targets = @{
    (Join-Path $site 'data') = $null
    (Join-Path $site 'assets\apps') = $null
    (Join-Path $site 'assets\screenshots') = $null
}
foreach ($dir in $targets.Keys) { New-Item -ItemType Directory -Force $dir | Out-Null }

Copy-Item (Join-Path $root 'catalog\software.json') (Join-Path $site 'data\catalog.json') -Force
Get-ChildItem (Join-Path $site 'assets\apps') -Filter *.png | Remove-Item
Copy-Item (Join-Path $root 'assets\icons\apps\*.png') (Join-Path $site 'assets\apps') -Force
Copy-Item (Join-Path $root 'assets\images\screenshots\*.png') (Join-Path $site 'assets\screenshots') -Force
Copy-Item (Join-Path $root 'assets\images\logo.svg') (Join-Path $site 'assets\logo.svg') -Force
Copy-Item (Join-Path $root 'assets\icons\deploynest-512.png') (Join-Path $site 'assets\logo-512.png') -Force

Write-Host "Website data synced to $site"
