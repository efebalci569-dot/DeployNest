#!/usr/bin/env bash
# Removes a per-user DeployNest installation made by install.sh.
# Settings, history and logs in ~/.local/share/DeployNest are kept unless --purge is given.
set -euo pipefail

data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
rm -rf "$HOME/.local/opt/deploynest"
rm -f "$HOME/.local/bin/deploynest" "$data_home/applications/deploynest.desktop" "$data_home/icons/hicolor/512x512/apps/deploynest.png"
if [[ "${1:-}" == "--purge" ]]; then
  rm -rf "$data_home/DeployNest"
fi
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database "$data_home/applications" >/dev/null 2>&1 || true
echo "DeployNest kaldırıldı. Kurduğun programlar etkilenmez."
