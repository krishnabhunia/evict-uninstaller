# Packages existing Windows builds with a verified native macOS disk image.
# First build Portable/Evict.exe and Installed/Evict-Setup-<version>.exe.
# Build the macOS installer on a Mac with make-app.sh --universal, then make-dmg.sh <version>
# Example: build/package.ps1 -MacOSArchive ..\macos\build\Evict_1.12.2.dmg
param(
    [string] $MacOSArchive,
    [string] $Version
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($MacOSArchive)) {
    throw "A complete release ZIP requires -MacOSArchive <verified Evict_<version>.dmg>. Build it on a Mac with Scripts/make-app.sh --universal and Scripts/make-dmg.sh <version>; empty macOS placeholders are not allowed."
}
$macArchive = Get-Item -LiteralPath $MacOSArchive -ErrorAction Stop
if ($macArchive.PSIsContainer -or ($macArchive.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $macArchive.Length -eq 0) {
    throw "MacOSArchive must be a nonempty ordinary native DMG file."
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
