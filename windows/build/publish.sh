#!/usr/bin/env bash
# Builds the single-file Windows executable from Linux/macOS/Windows (needs .NET 8 SDK).
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
dotnet test tests/Evict.Core.Tests/Evict.Core.Tests.csproj -c Release
python3 build/xaml_check.py || true
rm -rf publish
dotnet publish src/Evict.App/Evict.App.csproj -c Release -o publish
ls -la publish/Evict.exe
sha256sum publish/Evict.exe | tee publish/Evict.exe.sha256
