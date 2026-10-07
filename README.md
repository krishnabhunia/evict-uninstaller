# Evict Uninstaller

Uninstaller and leftover cleaner for **Windows** and **macOS**. The two apps are separate native programs
(no shared code). This repository holds both.

| SrNo. | Platform | Folder | Stack | Version source | Release tags | Docs |
|---|---|---|---|---|---|---|
| 1 | Windows 10/11 | [`windows/`](windows/) | C# / .NET 8 / WPF, Inno Setup | [Build metadata](windows/Directory.Build.props) | `win-vX.Y.Z` (`vX.Y.Z` up to 1.3.3) | [windows/README.md](windows/README.md) |
| 2 | macOS 13+ | [`macos/`](macos/) | Swift 5.9 / SwiftUI, SwiftPM | [Version.swift](macos/Sources/EvictKit/Version.swift) | `mac-vX.Y.Z` | [macos/README.md](macos/README.md) |

## Downloads

[Releases](https://github.com/krishnabhunia/evict-uninstaller/releases): `Evict.exe` / `Evict-Setup-x.y.z.exe`
for Windows, `Evict-x.y.z.zip` for macOS. Only Windows releases are marked *Latest*, because installed Windows copies
up to 1.3.3 update themselves from `releases/latest`.

## Building

| SrNo. | Platform | Commands (run inside the folder) | CI workflow |
|---|---|---|---|
| 1 | Windows | `dotnet test tests/Evict.Core.Tests` · `build/publish.sh` or `build/publish.ps1` | [`.github/workflows/windows.yml`](.github/workflows/windows.yml) |
| 2 | macOS | `swift test` · `Scripts/make-app.sh --universal` | [`.github/workflows/macos.yml`](.github/workflows/macos.yml) |

Each platform workflow runs when its own files or shared release automation change, and publishes only its own tag prefix.

## Automatic versioning and releases

GitHub Actions maintains versions as **x.y.z** and records the calculated version in source before testing and packaging the release.

| Change | PR title or commit example | From 1.8.0 |
| --- | --- | --- |
| Major update / breaking change | `feat!: redesign the uninstall workflow` | 2.0.0 |
| New feature | `feat: add a beta update channel` | 1.9.0 |
| Bug or error fix | `fix: handle interrupted downloads` | 1.8.1 |

The highest increment among unreleased changes wins. Major increments reset minor and patch; minor increments reset patch. Windows and macOS keep independent version tracks.

Use the PR title, a `release:major` / `release:minor` / `release:patch` label, or a `Release-Type:` description line to describe the change. Contributors do not need to edit version files or create version tags. Documentation and test changes alone do not publish a release.

After a merge to `main`, the workflow calculates the version, commits the managed version files and changelog, tests that exact source commit, and publishes matching assets. Published releases stay unchanged; failed publication can be retried from Actions on `main`.

Trusted Windows PRs publish optional beta previews such as `1.9.0-beta.11.23.1` before merge. Enable **Settings → Updates → Include beta releases** in a beta-aware Evict installation to receive these through the app.

See [automatic versioning](docs/versioning.md) for intent rules, scoped platform overrides, retry behavior and source provenance, and [beta updates](docs/beta-updates.md) for preview installation.

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
