<#
.SYNOPSIS
  Builds the release packages into dist\ (ignored by git – upload them as GitHub release assets).

.DESCRIPTION
  Windows: dist\PokePat-DecompKit-<version>-windows-x64.zip
           PokePatDecompKit.exe (graphical app, single file, no .NET installation needed) + cli\ + tools\ + README
  Linux:   dist\PokePat-DecompKit-<version>-linux-x64.tar.gz
           decompkit (command line, needs the .NET 8 runtime) + tools\ + README
  Runs the unit tests first unless -SkipTests is given.

.EXAMPLE
  pwsh -File release.ps1
  pwsh -File release.ps1 -SkipTests
#>
param([switch]$SkipTests)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = Join-Path $env:USERPROFILE ".dotnet\dotnet.exe"
if (Test-Path $userDotnet) { $dotnet = $userDotnet }
if (-not $dotnet) { throw ".NET 8 SDK not found (https://dotnet.microsoft.com/download/dotnet/8.0)." }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"; $env:DOTNET_NOLOGO = "1"


$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
if (-not $version) { $version = "0.0.0" }
# Build in a local folder: publishing from a \\wsl.localhost share is slow and single-file extraction can be blocked.
$work = Join-Path $env:TEMP "pokepat-decompkit-release"
$dist = Join-Path $PSScriptRoot "dist"
if (Test-Path $work) { Remove-Item -Recurse -Force $work }
New-Item -ItemType Directory -Force $work, $dist | Out-Null

# Publish from a copy on the local disk: self-contained single-file publishing adds packages (ILLink tasks) to the
# lock files, and the committed ones must stay untouched. It is also much faster than building on a \\wsl.localhost share.
$srcCopy = Join-Path $work "source"
& robocopy $PSScriptRoot $srcCopy /E /XD bin obj dist artifacts .git .vs TestResults /XF test-paths.local.txt /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copying the sources failed" }

if (-not $SkipTests) {
    & $dotnet test tests\TilesetEditor.Tests -c Release --filter "Category!=Integration"
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed – no release built." }
}

Push-Location $srcCopy
function Copy-Common($target) {
    Copy-Item README.md, LICENSE $target
    New-Item -ItemType Directory -Force (Join-Path $target "tools") | Out-Null
    Copy-Item tools\porytiles.lock.json, tools\setup-porytiles.ps1, tools\setup-porytiles.sh (Join-Path $target "tools")
    Copy-Item docs (Join-Path $target "docs") -Recurse -Exclude "OFFEN.md"
}

# ---------------- Windows
$win = Join-Path $work "PokePat-DecompKit-$version-windows-x64"
& $dotnet publish src\TilesetEditor.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o (Join-Path $work "app")
if ($LASTEXITCODE -ne 0) { throw "publish (Windows app) failed" }
& $dotnet publish src\TilesetEditor.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o (Join-Path $win "cli")
if ($LASTEXITCODE -ne 0) { throw "publish (Windows CLI) failed" }
Copy-Item (Join-Path $work "app\TilesetEditor.exe") (Join-Path $win "PokePatDecompKit.exe")
Rename-Item (Join-Path $win "cli\tileset-editor-cli.exe") "decompkit.exe"
Copy-Common $win
$winZip = Join-Path $dist "PokePat-DecompKit-$version-windows-x64.zip"
if (Test-Path $winZip) { Remove-Item $winZip }
Compress-Archive -Path $win -DestinationPath $winZip

# ---------------- Linux (command line; needs the .NET 8 runtime)
$linName = "PokePat-DecompKit-$version-linux-x64"
$lin = Join-Path $work $linName
& $dotnet publish src\TilesetEditor.Cli -c Release -p:UseAppHost=false -p:DebugType=none -o (Join-Path $lin "lib")
if ($LASTEXITCODE -ne 0) { throw "publish (Linux CLI) failed" }
Copy-Item packaging\decompkit $lin
Copy-Common $lin
$linTar = Join-Path $dist "$linName.tar.gz"
if (Test-Path $linTar) { Remove-Item $linTar }
# Windows tar cannot store the Unix execute bit: the README tells Linux users to run "chmod +x decompkit" (or "bash decompkit").
& tar.exe -czf $linTar -C $work $linName
if ($LASTEXITCODE -ne 0) { throw "tar failed" }

Pop-Location
Remove-Item -Recurse -Force $srcCopy
Write-Host ""
Write-Host "Release packages:"
Get-ChildItem $dist | ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
