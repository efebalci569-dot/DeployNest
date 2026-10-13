#!/usr/bin/env bash
# Installs DeployNest for the current user (no root needed):
#   ~/.local/opt/deploynest          application files
#   ~/.local/bin/deploynest          command
#   ~/.local/share/applications/     menu entry
set -euo pipefail

source_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
target="$HOME/.local/opt/deploynest"
bin_dir="$HOME/.local/bin"

echo "DeployNest kuruluyor: $target"
rm -rf "$target"
mkdir -p "$target" "$bin_dir" "$data_home/applications" "$data_home/icons/hicolor/512x512/apps"
cp -a "$source_dir"/. "$target"/
chmod +x "$target/DeployNest"
ln -sf "$target/DeployNest" "$bin_dir/deploynest"
cp "$source_dir/deploynest.png" "$data_home/icons/hicolor/512x512/apps/deploynest.png"
sed "s|^Exec=.*|Exec=$target/DeployNest|" "$source_dir/deploynest.desktop" > "$data_home/applications/deploynest.desktop"

command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data_home/applications" >/dev/null 2>&1 || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -t "$data_home/icons/hicolor" >/dev/null 2>&1 || true

echo "Tamam. DeployNest'i uygulama menüsünden veya terminalde 'deploynest' yazarak açabilirsin."
case ":$PATH:" in
  *":$bin_dir:"*) ;;
  *) echo "Not: $bin_dir PATH içinde değil; menüden açabilir veya oturumu kapatıp açabilirsin." ;;
esac
