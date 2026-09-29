# Evict Uninstaller

Uninstaller and leftover cleaner for **Windows** and **macOS**. The two apps are separate native programs
(no shared code). This repository holds both.

| SrNo. | Platform | Folder | Stack | Current version | Release tags | Docs |
|---|---|---|---|---|---|---|
| 1 | Windows 10/11 | [`windows/`](windows/) | C# / .NET 8 / WPF, Inno Setup | 1.6.0 | `win-vX.Y.Z` (`vX.Y.Z` up to 1.3.3) | [windows/README.md](windows/README.md) |
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

## Versioning

Both apps use **Semantic Versioning** – `MAJOR.MINOR.PATCH`. The number changes **once per release** (not per pull
request); the biggest change in the release decides the bump, and a number is **never published twice** (the in-app
update check compares numbers, so a PC that already has x.y.z never takes a different x.y.z).

| SrNo. | Part | Increase when the release contains… | Example | Reset |
|---|---|---|---|---|
| 1 | **PATCH** (`x.y.Z`) | only bug fixes | 1.6.0 → 1.6.1 | – |
| 2 | **MINOR** (`x.Y.0`) | at least one new feature, nothing that stops working the old way | 1.6.1 → 1.7.0 | PATCH → 0 |
| 3 | **MAJOR** (`X.0.0`) | a breaking change: dropped Windows / macOS version, settings or history older versions cannot read, a removed feature or command-line option | 1.7.0 → 2.0.0 | MINOR, PATCH → 0 |

Test builds from CI carry the version in the code at that moment. If a test build was installed somewhere, the next
release must use a higher number than that build, or that PC is never offered the update.

## Releasing

| SrNo. | Step | Windows | macOS |
|---|---|---|---|
| 1 | Decide the bump (table above) | – | – |
| 2 | Set the version | `windows/Directory.Build.props` → `<Version>`; `windows/installer/Evict.iss` default `MyAppVersion`; the table at the top of this file | `macos/Sources/EvictKit/Version.swift`; the table at the top of this file |
| 3 | Changelog | `windows/CHANGELOG.md`: new `## X.Y.Z — title (date)` heading | `macos/CHANGELOG.md` |
| 4 | Pull request → CI green → merge | `.github/workflows/windows.yml` | `.github/workflows/macos.yml` |
| 5 | Publish | Releases → *Draft a new release* → new tag `win-vX.Y.Z` on `main` → *Publish*, or `git tag win-vX.Y.Z && git push origin win-vX.Y.Z` | same with `mac-vX.Y.Z` |
| 6 | CI attaches the files | `Evict.exe`, `Evict-Setup-X.Y.Z.exe` + `.sha256` (marked **Latest**) | `Evict-X.Y.Z.zip` + `.sha256` (never "Latest") |
| 7 | Check | Installed copies show "Evict X.Y.Z is available" at their next start | – |

**Asking Claude Code for a release:** say what should ship (for example *"prepare a Windows release with the fixes on
main"*). It lists the changes since the last published tag, picks the bump with the table above (fixes only → PATCH,
any feature → MINOR, anything breaking → MAJOR – and above any installed test build), does steps 2–4 in a pull
request, and after you merge and publish the tag (step 5) verifies the release (steps 6–7).

## Build output

| SrNo. | Folder | Contains |
|---|---|---|
| 1 | `windows/Portable/` | `Evict.exe` – stand-alone, runs from anywhere, nothing installed |
| 2 | `windows/Installed/` | `Evict-Setup-X.Y.Z.exe` – installs Evict (Start menu, Apps & features, uninstaller) |

Local builds and the CI download use these two folders; a GitHub Release lists the four files side by side.

## History

This repository was `krishnabhunia/evict` (Windows only) and was renamed to `evict-uninstaller` on 29 Sep 2026; GitHub
redirects the old URL. The macOS app was imported from `krishnabhunia/evict-mac` with its full commit history
(its tag `v0.1.0` is `mac-v0.1.0` here).
