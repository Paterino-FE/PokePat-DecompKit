#!/usr/bin/env bash
# Builds the Windows app from WSL/Linux (cross-compile) for quick testing, and optionally the release packages.
#
#   ./dev-build.sh              -> testbuild/      unpacked Windows build: PokePatDecompKit.exe + cli\decompkit.exe
#   ./dev-build.sh --release    -> testbuild/ and release/  (…-windows-x64.zip and …-linux-x64.tar.gz)
#   ./dev-build.sh --tests      also run the unit tests first (need a reference project, see test-paths.example.txt)
#
# Both folders are ignored by git. Content is the same as the release packages built by release.ps1.
# Needs the .NET 8 SDK (dotnet on PATH, or installed in ~/.dotnet by dotnet-install.sh).
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
root="$PWD"

release=false; tests=false
for arg in "$@"; do
  case "$arg" in
    --release) release=true ;;
    --tests) tests=true ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

if [ -x "$HOME/.dotnet/dotnet" ]; then export DOTNET_ROOT="$HOME/.dotnet"; export PATH="$DOTNET_ROOT:$PATH"; fi
command -v dotnet >/dev/null || { echo ".NET 8 SDK not found (https://dotnet.microsoft.com/download/dotnet/8.0)." >&2; exit 1; }
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if ! ldconfig -p 2>/dev/null | grep -q libicuuc; then
  echo "Note: libicu not installed (sudo apt install libicu-dev) – running the SDK in invariant globalization mode." >&2
  export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
fi

version=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -n1)
version=${version:-0.0.0}

# Work on a copy: self-contained single-file publishing adds packages to the lock files, the committed ones stay untouched.
work=$(mktemp -d -t pokepat-devbuild.XXXXXX)
trap 'rm -rf "$work"' EXIT
rsync -a --exclude .git --exclude bin --exclude obj --exclude dist --exclude artifacts --exclude TestResults \
  --exclude testbuild --exclude release ./ "$work/source/"
cd "$work/source"

if $tests; then
  dotnet test tests/TilesetEditor.Tests -c Release --filter "Category!=Integration" -p:EnableWindowsTargeting=true
fi

common() {  # README, LICENSE, tools, docs – same as release.ps1
  cp README.md LICENSE "$1/"
  mkdir -p "$1/tools"
  cp tools/porytiles.lock.json tools/setup-porytiles.ps1 tools/setup-porytiles.sh "$1/tools/"
  mkdir -p "$1/docs"; rsync -a --exclude OFFEN.md docs/ "$1/docs/"
  mkdir -p "$1/mods"; rsync -a mods/ "$1/mods/"   # mods shipped with PokePat (Mods module)
}

# ---------------- Windows
win="$work/PokePat-DecompKit-$version-windows-x64"
dotnet publish src/TilesetEditor.App -c Release -r win-x64 --self-contained true -p:EnableWindowsTargeting=true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true \
  -p:DebugType=none -o "$work/app"
dotnet publish src/TilesetEditor.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o "$win/cli"
cp "$work/app/TilesetEditor.exe" "$win/PokePatDecompKit.exe"
mv "$win/cli/tileset-editor-cli.exe" "$win/cli/decompkit.exe"
common "$win"

rm -rf "$root/testbuild"
cp -r "$win" "$root/testbuild"

if $release; then
  # ---------------- Linux (command line; needs the .NET 8 runtime)
  linName="PokePat-DecompKit-$version-linux-x64"
  lin="$work/$linName"
  dotnet publish src/TilesetEditor.Cli -c Release -p:UseAppHost=false -p:DebugType=none -o "$lin/lib"
  cp packaging/decompkit "$lin/"
  common "$lin"
  chmod +x "$lin/decompkit" "$lin/tools/setup-porytiles.sh"   # tar keeps the execute bit (unlike the Windows build)

  mkdir -p "$root/release"
  winZip="$root/release/PokePat-DecompKit-$version-windows-x64.zip"
  rm -f "$winZip" "$root/release/$linName.tar.gz"
  (cd "$work" && python3 -I -m zipfile -c "$winZip" "$(basename "$win")")
  tar -czf "$root/release/$linName.tar.gz" -C "$work" "$linName"
fi

echo
echo "Test build: $root/testbuild/PokePatDecompKit.exe"
if $release; then echo "Release packages:"; ls -lh "$root/release" | tail -n +2; fi
