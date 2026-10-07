# Packages existing Windows builds with a real macOS app archive.
# First build Portable/Evict.exe and Installed/Evict-Setup-<version>.exe.
# Build the macOS ZIP on a Mac with: macos/Scripts/make-app.sh --universal
# Example: build/package.ps1 -MacOSArchive ..\macos\build\Evict-0.2.0.zip
param(
    [string] $MacOSArchive,
    [string] $Version
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($MacOSArchive)) {
    throw "A complete release ZIP requires -MacOSArchive <built Evict.app ZIP>. Build it on a Mac with Scripts/make-app.sh --universal; empty macOS placeholders are not allowed."
}
$macArchive = Get-Item -LiteralPath $MacOSArchive -ErrorAction Stop
if ($macArchive.PSIsContainer -or ($macArchive.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $macArchive.Length -eq 0) {
    throw "MacOSArchive must be a nonempty ordinary app ZIP file."
}
$windowsRoot = (Get-Item -LiteralPath (Join-Path $PSScriptRoot "..")).FullName
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = ([xml](Get-Content -LiteralPath (Join-Path $windowsRoot "Directory.Build.props"))).Project.PropertyGroup.Version
}
$arguments = @(
    (Join-Path $PSScriptRoot "release_zip.py"), $Version,
    "--setup", (Join-Path $windowsRoot "Installed"),
    "--exe", (Join-Path $windowsRoot "Portable"),
    "--macos", $macArchive.FullName,
    "--out", (Join-Path $windowsRoot "Zip")
)
& python @arguments
if ($LASTEXITCODE -ne 0) {
    throw "Release ZIP validation or packaging failed with exit code $LASTEXITCODE."
}
