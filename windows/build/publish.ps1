# Builds the single-file Windows executable (needs .NET 8 SDK: winget install Microsoft.DotNet.SDK.8)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
dotnet test tests/Evict.Core.Tests/Evict.Core.Tests.csproj -c Release
if (Test-Path Portable) { Remove-Item Portable -Recurse -Force }
dotnet publish src/Evict.App/Evict.App.csproj -c Release -o Portable
Get-Item Portable/Evict.exe | Format-List Name, Length, LastWriteTime
(Get-FileHash Portable/Evict.exe -Algorithm SHA256).Hash | Out-File Portable/Evict.exe.sha256
