#!/usr/bin/env bash
# Builds the Linux release packages for one architecture:
#   DeployNest-<version>-linux-<arch>.tar.gz   portable folder + install.sh (any distribution)
#   deploynest_<version>_<debarch>.deb          Debian / Ubuntu / Linux Mint package (when dpkg-deb exists)
# and appends their SHA-256 sums to SHA256SUMS-linux.txt.
#
# Usage: ./scripts/publish-linux.sh 0.2.0 [linux-x64|linux-arm64]
set -euo pipefail

version="${1:?Kullanım: publish-linux.sh <sürüm> [linux-x64|linux-arm64]}"
rid="${2:-linux-x64}"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] || { echo "Geçersiz sürüm: $version" >&2; exit 1; }
case "$rid" in
  linux-x64) arch="x64"; debarch="amd64" ;;
  linux-arm64) arch="arm64"; debarch="arm64" ;;
  *) echo "Desteklenmeyen hedef: $rid" >&2; exit 1 ;;
esac

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/artifacts/release"
publish="$root/artifacts/publish/$rid"
name="DeployNest-$version-linux-$arch"
stage="$root/artifacts/linux/$name"

rm -rf "$publish" "$stage" "$root/artifacts/linux/deb-$arch"
mkdir -p "$out" "$stage"

dotnet publish "$root/src/DeployNest.Linux/DeployNest.Linux.csproj" \
  -c Release -r "$rid" --self-contained true \
  -p:PublishSingleFile=true -p:Version="$version" \
  -o "$publish"

# Application folder: single-file executable, native Skia/HarfBuzz libraries and desktop integration.
cp "$publish/DeployNest" "$stage/"
cp "$publish"/*.so "$stage/" 2>/dev/null || true
cp "$root/packaging/linux/deploynest.desktop" "$root/packaging/linux/install.sh" "$root/packaging/linux/uninstall.sh" "$root/packaging/linux/README.txt" "$stage/"
cp "$root/assets/icons/deploynest-512.png" "$stage/deploynest.png"
cp "$root/LICENSE" "$root/THIRD-PARTY-NOTICES.md" "$stage/"
chmod 755 "$stage/DeployNest" "$stage/install.sh" "$stage/uninstall.sh"
chmod 644 "$stage"/*.so 2>/dev/null || true

tar -C "$root/artifacts/linux" --owner=0 --group=0 -czf "$out/$name.tar.gz" "$name"

if command -v dpkg-deb >/dev/null 2>&1; then
  deb="$root/artifacts/linux/deb-$arch"
  mkdir -p "$deb/DEBIAN" "$deb/opt/deploynest" "$deb/usr/bin" "$deb/usr/share/applications" "$deb/usr/share/icons/hicolor/512x512/apps"
  cp "$stage/DeployNest" "$stage"/*.so "$stage/LICENSE" "$stage/THIRD-PARTY-NOTICES.md" "$deb/opt/deploynest/" 2>/dev/null || cp "$stage/DeployNest" "$stage/LICENSE" "$stage/THIRD-PARTY-NOTICES.md" "$deb/opt/deploynest/"
  ln -s /opt/deploynest/DeployNest "$deb/usr/bin/deploynest"
  cp "$stage/deploynest.desktop" "$deb/usr/share/applications/deploynest.desktop"
  cp "$stage/deploynest.png" "$deb/usr/share/icons/hicolor/512x512/apps/deploynest.png"
  installed_kb=$(du -sk "$deb" | cut -f1)
  cat > "$deb/DEBIAN/control" <<CONTROL
Package: deploynest
Version: $version
Section: admin
Priority: optional
Architecture: $debarch
Maintainer: DeployNest contributors <https://github.com/efebalci569-dot/DeployNest>
Installed-Size: $installed_kb
Depends: libc6, libstdc++6, libfontconfig1, libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu70 | libicu67 | libicu66
Recommends: flatpak, pkexec | policykit-1
Homepage: https://deploynest.vercel.app
Description: Yeni bilgisayar için toplu program kurulumu
 DeployNest, ihtiyacın olan programları seçip tek seferde kurmanı sağlar.
 Programlar Flathub'dan veya dağıtımın resmi paket deposundan doğrulanarak kurulur.
CONTROL
  chmod 755 "$deb/DEBIAN" "$deb/opt/deploynest/DeployNest"
  dpkg-deb --root-owner-group --build "$deb" "$out/deploynest_${version}_${debarch}.deb" >/dev/null
fi

# Replace earlier sums of the same files so re-running the script does not duplicate lines.
sums="$out/SHA256SUMS-linux.txt"
touch "$sums"
grep -v -F -e "  $name.tar.gz" -e "  deploynest_${version}_${debarch}.deb" "$sums" > "$sums.tmp" || true
(
  cd "$out"
  for file in "$name.tar.gz" "deploynest_${version}_${debarch}.deb"; do
    if [[ -f "$file" ]]; then sha256sum "$file"; fi
  done
) >> "$sums.tmp"
mv "$sums.tmp" "$sums"

ls -la "$out"
