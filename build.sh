#!/usr/bin/env bash
# Linux/macOS: builds and tests the libraries and the command line tool (the graphical app is Windows-only).
# Needs the .NET 8 SDK (https://dotnet.microsoft.com/download/dotnet/8.0).
# Usage: ./build.sh [--skip-tests]      Result: artifacts/linux/decompkit
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
dotnet build src/TilesetEditor.Cli -c Release
if [ "${1:-}" != "--skip-tests" ]; then
  dotnet test tests/TilesetEditor.Tests -c Release --filter "Category!=Integration"
fi
out=artifacts/linux
rm -rf "$out"
dotnet publish src/TilesetEditor.Cli -c Release -p:UseAppHost=false -p:DebugType=none -o "$out/lib"
cp packaging/decompkit "$out/decompkit"
chmod +x "$out/decompkit"
echo "Done. Start with: $out/decompkit setup"
