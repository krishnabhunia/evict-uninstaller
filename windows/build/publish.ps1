# Builds the single-file Windows executable (needs .NET 8 SDK: winget install Microsoft.DotNet.SDK.8)
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
dotnet test tests/Evict.Core.Tests/Evict.Core.Tests.csproj -c Release
if (Test-Path publish) { Remove-Item publish -Recurse -Force }
dotnet publish src/Evict.App/Evict.App.csproj -c Release -o publish
Get-Item publish/Evict.exe | Format-List Name, Length, LastWriteTime
(Get-FileHash publish/Evict.exe -Algorithm SHA256).Hash | Out-File publish/Evict.exe.sha256
