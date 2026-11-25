<#
.SYNOPSIS
  Installs the pinned Porytiles release (tools/porytiles.lock.json) into a WSL distribution.

.DESCRIPTION
  Downloads the Linux release on the Windows side (works even if WSL itself has no internet access),
  verifies the SHA-256 checksum, unpacks it and copies it into WSL. Re-running is safe.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\setup-porytiles.ps1
  powershell -ExecutionPolicy Bypass -File tools\setup-porytiles.ps1 -Distro Ubuntu -ZipPath D:\Downloads\porytiles-linux-amd64.zip
#>
param(
    [string]$Distro = "Ubuntu",
    # Use an already downloaded zip (offline / behind a proxy). Its checksum is still verified.
    [string]$ZipPath = ""
)
$ErrorActionPreference = "Stop"
$lock = Get-Content (Join-Path $PSScriptRoot "porytiles.lock.json") -Raw | ConvertFrom-Json
$work = Join-Path $env:TEMP "tileseteditor-porytiles-$($lock.version)"
New-Item -ItemType Directory -Force $work | Out-Null

if (-not $ZipPath) {
    $ZipPath = Join-Path $work $lock.asset
    if (-not (Test-Path $ZipPath)) {
        Write-Host "Downloading $($lock.url) ..."
        $ProgressPreference = "SilentlyContinue"
        Invoke-WebRequest $lock.url -OutFile $ZipPath
    }
}
$hash = (Get-FileHash $ZipPath -Algorithm SHA256).Hash
if ($hash -ne $lock.sha256) {
    throw "Checksum mismatch: expected $($lock.sha256), got $hash. File: $ZipPath"
}
Write-Host "Checksum OK ($hash)."

$extract = Join-Path $work "x"
if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
Expand-Archive $ZipPath -DestinationPath $extract

if ($extract -notmatch '^([A-Za-z]):\\(.*)$') { throw "TEMP must be on a local drive: $extract" }
$linuxSrc = "/mnt/$($Matches[1].ToLower())/$($Matches[2].Replace('\', '/'))"
& wsl.exe -d $Distro -- true
if ($LASTEXITCODE -ne 0) { throw "WSL distribution '$Distro' not available (check wsl -l -v)." }
$dest = $lock.wsl_install_dir
$script = "set -e; mkdir -p $dest; cp -r '$linuxSrc'/* $dest/; chmod +x $dest/porytiles-linux-amd64/porytiles; $($lock.executable) --version"
& wsl.exe -d $Distro -- bash -c $script
if ($LASTEXITCODE -ne 0) { throw "Installation in WSL failed." }
Write-Host "Porytiles $($lock.version) installed: $($lock.executable) (WSL: $Distro)"
