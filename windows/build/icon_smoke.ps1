# Reads icon resources only; never launches the packaged executable.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$IconPath,
    [Parameter(Mandatory = $true)][string]$ExecutablePath
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Native icon validation requires Windows.' }
Add-Type -AssemblyName System.Drawing
if (-not ('EvictIconExtractionNative' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class EvictIconExtractionNative {
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconExW")]
    public static extern uint Extract(string path, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr handle);
}
'@
}

function Get-PixelFingerprint([IntPtr]$Handle) {
    $icon = [Drawing.Icon]::FromHandle($Handle)
    try {
        $bitmap = $icon.ToBitmap()
        try {
            $pixels = [Text.StringBuilder]::new()
            $null = $pixels.Append($bitmap.Width).Append(':').Append($bitmap.Height).Append(':')
            $visible = 0
            for ($y = 0; $y -lt $bitmap.Height; $y++) {
                for ($x = 0; $x -lt $bitmap.Width; $x++) {
                    $pixel = $bitmap.GetPixel($x, $y)
                    if ($pixel.A -gt 0) { $visible++ }
                    $null = $pixels.Append($pixel.ToArgb().ToString('X8'))
                }
            }
            if ($visible -eq 0) { throw 'Extracted icon is entirely transparent.' }
            return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($pixels.ToString())))
        } finally { $bitmap.Dispose() }
    } finally { $icon.Dispose() }
}

function Get-ExtractedFingerprints([string]$Path) {
    $large = [IntPtr]::Zero
    $small = [IntPtr]::Zero
    try {
        $count = [EvictIconExtractionNative]::Extract($Path, 0, [ref]$large, [ref]$small, 1)
        if ($count -eq 0 -or $large -eq [IntPtr]::Zero -or $small -eq [IntPtr]::Zero) {
            throw "Could not extract both Windows icon sizes from $Path"
        }
        return @{
            Large = Get-PixelFingerprint $large
            Small = Get-PixelFingerprint $small
        }
    } finally {
        if ($large -ne [IntPtr]::Zero) { $null = [EvictIconExtractionNative]::DestroyIcon($large) }
        if ($small -ne [IntPtr]::Zero -and $small -ne $large) { $null = [EvictIconExtractionNative]::DestroyIcon($small) }
    }
}

$source = Get-ExtractedFingerprints (Resolve-Path -LiteralPath $IconPath).Path
$published = Get-ExtractedFingerprints (Resolve-Path -LiteralPath $ExecutablePath).Path
foreach ($size in @('Small', 'Large')) {
    if ($source[$size] -cne $published[$size]) {
        throw "Published executable $size icon differs from the Evict source artwork."
    }
}
Write-Host 'Native icon smoke passed: published small and large icons match the source artwork.'
