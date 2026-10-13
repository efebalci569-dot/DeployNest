#!/usr/bin/env bash
# Copies the catalog, program logos, app logo and screenshots into website/ so the static site
# (deployed by Vercel from the website/ folder) always shows the same data as the app.
# Same as scripts/build-website.ps1, for Linux and macOS.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
site="$root/website"

mkdir -p "$site/data" "$site/assets/apps" "$site/assets/screenshots"

cp "$root/catalog/software.json" "$site/data/catalog.json"
find "$site/assets/apps" -maxdepth 1 -name '*.png' -delete
cp "$root"/assets/icons/apps/*.png "$site/assets/apps/"
cp "$root"/assets/images/screenshots/*.png "$site/assets/screenshots/"
cp "$root/assets/images/logo.svg" "$site/assets/logo.svg"
cp "$root/assets/icons/deploynest-512.png" "$site/assets/logo-512.png"

echo "Website data synced to $site"
