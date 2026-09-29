# Evict Uninstaller

Uninstaller and leftover cleaner for **Windows** and **macOS**. The two apps are separate native programs
(no shared code). This repository holds both.

| SrNo. | Platform | Folder | Stack | Current version | Release tags | Docs |
|---|---|---|---|---|---|---|
| 1 | Windows 10/11 | [`windows/`](windows/) | C# / .NET 8 / WPF, Inno Setup | 1.4.0 | `win-vX.Y.Z` (`vX.Y.Z` up to 1.3.3) | [windows/README.md](windows/README.md) |
| 2 | macOS 13+ | [`macos/`](macos/) | Swift 5.9 / SwiftUI, SwiftPM | 0.1.0 | `mac-vX.Y.Z` | [macos/README.md](macos/README.md) |

## Downloads

[Releases](https://github.com/krishnabhunia/evict-uninstaller/releases): `Evict.exe` / `Evict-Setup-x.y.z.exe`
for Windows, `Evict-x.y.z.zip` for macOS. Only Windows releases are marked *Latest*, because installed Windows copies
up to 1.3.3 update themselves from `releases/latest`.

## Building

| SrNo. | Platform | Commands (run inside the folder) | CI workflow |
|---|---|---|---|
| 1 | Windows | `dotnet test tests/Evict.Core.Tests` · `build/publish.sh` or `build/publish.ps1` | [`.github/workflows/windows.yml`](.github/workflows/windows.yml) |
| 2 | macOS | `swift test` · `Scripts/make-app.sh --universal` | [`.github/workflows/macos.yml`](.github/workflows/macos.yml) |

Each workflow runs only when its own folder (or the workflow file) changes, and publishes a GitHub Release only
for its own tag prefix.

## Releasing

| SrNo. | Platform | Bump the version in | Then |
|---|---|---|---|
| 1 | Windows | `windows/Directory.Build.props`, `windows/CHANGELOG.md` | `git tag win-v1.3.4 && git push origin win-v1.3.4` |
| 2 | macOS | `macos/Sources/EvictKit/Version.swift`, `macos/CHANGELOG.md` | `git tag mac-v0.1.1 && git push origin mac-v0.1.1` |

## History

This repository was `krishnabhunia/evict` (Windows only) and was renamed to `evict-uninstaller` on 29 Sep 2026; GitHub
redirects the old URL. The macOS app was imported from `krishnabhunia/evict-mac` with its full commit history
(its tag `v0.1.0` is `mac-v0.1.0` here).
