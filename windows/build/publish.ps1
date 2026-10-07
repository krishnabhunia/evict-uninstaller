# Builds the single-file Windows executable (needs .NET 8 SDK: winget install Microsoft.DotNet.SDK.8)
$ErrorActionPreference = "Stop"
$windowsRoot = (Get-Item -LiteralPath (Join-Path $PSScriptRoot "..")).FullName
$portableDirectory = [IO.Path]::GetFullPath((Join-Path $windowsRoot "Portable"))
if (![string]::Equals([IO.Path]::GetDirectoryName($portableDirectory), $windowsRoot.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
    throw "The publish output must be directly inside the Windows project directory."
}
Set-Location -LiteralPath $windowsRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
dotnet test tests/Evict.Core.Tests/Evict.Core.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Unit tests failed with exit code $LASTEXITCODE. Publishing stopped." }
if (Test-Path -LiteralPath $portableDirectory) {
    $outputDirectory = Get-Item -LiteralPath $portableDirectory -Force
    if (!$outputDirectory.PSIsContainer -or ($outputDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing to replace a publish output that is not an ordinary directory."
    }
    Remove-Item -LiteralPath $portableDirectory -Recurse -Force
}
dotnet publish src/Evict.App/Evict.App.csproj -c Release -o $portableDirectory
if ($LASTEXITCODE -ne 0) { throw "Publishing failed with exit code $LASTEXITCODE." }
$portableExecutable = Join-Path $portableDirectory "Evict.exe"
Get-Item -LiteralPath $portableExecutable | Format-List Name, Length, LastWriteTime
(Get-FileHash -LiteralPath $portableExecutable -Algorithm SHA256).Hash | Out-File -LiteralPath ($portableExecutable + ".sha256")
