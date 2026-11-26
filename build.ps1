<#
.SYNOPSIS
  Reproducible Windows build of the PokéPat DecompKit.

.DESCRIPTION
  Uses the SDK pinned in global.json and the locked NuGet graph (packages.lock.json).
  Default: restore (locked), build Release, run unit tests, publish the app.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build.ps1
  powershell -ExecutionPolicy Bypass -File build.ps1 -IntegrationTests      # also runs Porytiles via WSL
  powershell -ExecutionPolicy Bypass -File build.ps1 -SkipTests -Output D:\Tools\PokePatDecompKit
#>
param(
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$IntegrationTests,
    # Where the runnable app goes. Default: artifacts\app, or %LOCALAPPDATA%\PokePatDecompKit\app when the repo
    # lives on a network/WSL share (Windows blocks or warns about starting programs from \\ paths).
    [string]$Output = ""
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (Test-Path $userDotnet) { $dotnet = $userDotnet }
if (-not $dotnet) { throw ".NET 8 SDK not found. See docs\README.md (setup section)." }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"
& $dotnet --version | Out-Null; if ($LASTEXITCODE -ne 0) { throw "The SDK pinned in global.json (8.0.x) is missing." }

if (-not $Output) {
    $Output = if ($PSScriptRoot.StartsWith("\\")) { Join-Path $env:LOCALAPPDATA "PokePatDecompKit\app" } else { Join-Path $PSScriptRoot "artifacts\app" }
}

& $dotnet restore TilesetEditor.sln --locked-mode
if ($LASTEXITCODE -ne 0) { throw "restore failed" }
& $dotnet build TilesetEditor.sln -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { throw "build failed" }

if (-not $SkipTests) {
    & $dotnet test tests\TilesetEditor.Tests -c $Configuration --no-build --filter "Category!=Integration"
    if ($LASTEXITCODE -ne 0) { throw "unit tests failed" }
}
if ($IntegrationTests) {
    & $dotnet test tests\TilesetEditor.Tests -c $Configuration --no-build --filter "Category=Integration"
    if ($LASTEXITCODE -ne 0) { throw "integration tests failed" }
}

& $dotnet publish src\TilesetEditor.App -c $Configuration -r win-x64 --self-contained false -o $Output
if ($LASTEXITCODE -ne 0) { throw "publish (App) failed" }
& $dotnet publish src\TilesetEditor.Cli -c $Configuration -r win-x64 --self-contained false -o (Join-Path $Output "cli")
if ($LASTEXITCODE -ne 0) { throw "publish (CLI) failed" }
Copy-Item (Join-Path $PSScriptRoot "mods") (Join-Path $Output "mods") -Recurse -Force   # mods shipped with PokePat
Write-Host ""
Write-Host "Done. Run: $(Join-Path $Output 'TilesetEditor.exe')"
