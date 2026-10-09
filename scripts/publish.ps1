<#
.SYNOPSIS
  Builds the release packages: single-file EXE, portable ZIP and SHA256SUMS.txt.
.EXAMPLE
  ./scripts/publish.ps1 -Version 0.1.0
#>
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string]$Version,
    [string]$Runtime = 'win-x64',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\release' }
$publishDir = Join-Path $root "artifacts\publish\$Runtime"

Remove-Item -Recurse -Force $publishDir, $OutputDirectory -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $OutputDirectory | Out-Null

dotnet publish (Join-Path $root 'src\DeployNest.App\DeployNest.App.csproj') `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:Version=$Version `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

$exeName = "DeployNest-$Version-$Runtime.exe"
$exe = Join-Path $OutputDirectory $exeName
Copy-Item (Join-Path $publishDir 'DeployNest.exe') $exe

$staging = Join-Path $root "artifacts\portable\DeployNest-$Version"
Remove-Item -Recurse -Force $staging -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $staging | Out-Null
Copy-Item (Join-Path $publishDir 'DeployNest.exe') $staging
Copy-Item (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') $staging
$zip = Join-Path $OutputDirectory "DeployNest-$Version-$Runtime-portable.zip"
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -Force

$sums = Get-ChildItem $OutputDirectory -File | Where-Object Name -ne 'SHA256SUMS.txt' | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
Set-Content (Join-Path $OutputDirectory 'SHA256SUMS.txt') $sums -Encoding ASCII

Get-ChildItem $OutputDirectory | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
