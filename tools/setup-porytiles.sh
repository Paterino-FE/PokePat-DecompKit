#!/usr/bin/env bash
# Installs the pinned Porytiles release (tools/porytiles.lock.json) on Linux.
# Usage: tools/setup-porytiles.sh [path/to/porytiles-linux-amd64.zip]   (zip optional, e.g. when offline)
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
lock="$here/porytiles.lock.json"
get() { sed -n "s/.*\"$1\": *\"\([^\"]*\)\".*/\1/p" "$lock"; }
version="$(get version)"; url="$(get url)"; sha="$(get sha256 | tr 'A-F' 'a-f')"; asset="$(get asset)"
dest="$HOME/.local/share/tileseteditor/porytiles-$version"
zip="${1:-}"
if [ -z "$zip" ]; then
  zip="$(mktemp -d)/$asset"
  echo "Downloading $url ..."
  curl -fL -o "$zip" "$url"
fi
actual="$(sha256sum "$zip" | cut -d' ' -f1)"
[ "$actual" = "$sha" ] || { echo "Checksum mismatch: expected $sha, got $actual" >&2; exit 1; }
echo "Checksum OK."
mkdir -p "$dest"
unzip -o -q "$zip" -d "$dest"
chmod +x "$dest/porytiles-linux-amd64/porytiles"
"$dest/porytiles-linux-amd64/porytiles" --version
echo "Installed: $dest/porytiles-linux-amd64/porytiles"
echo "Use this path in: decompkit setup  (step 5, Porytiles)"
