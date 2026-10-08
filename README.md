# Evict Uninstaller

Uninstaller and leftover cleaner for **Windows** and **macOS**. The two apps are separate native programs
(no shared code). This repository holds both.

| SrNo. | Platform | Folder | Stack | Version source | Release tags | Docs |
|---|---|---|---|---|---|---|
| 1 | Windows 10/11 | [`windows/`](windows/) | C# / .NET 8 / WPF, Inno Setup | [Build metadata](windows/Directory.Build.props) | `win-vX.Y.Z` (`vX.Y.Z` up to 1.3.3) | [windows/README.md](windows/README.md) |
| 2 | macOS 13+ | [`macos/`](macos/) | Swift 5.9 / SwiftUI, SwiftPM | [Version.swift](macos/Sources/EvictKit/Version.swift) | `mac-vX.Y.Z` | [macos/README.md](macos/README.md) |

## Downloads

[Releases](https://github.com/krishnabhunia/evict-uninstaller/releases): download the installation ZIP for the three platform folders, or choose the separate `Evict.exe` / `Evict-Setup-x.y.z.exe` Windows updater assets. Only Windows releases are marked *Latest*, because installed Windows copies
up to 1.3.3 update themselves from `releases/latest`.

## Building

| SrNo. | Platform | Commands (run inside the folder) | CI workflow |
|---|---|---|---|
| 1 | Windows | `dotnet test tests/Evict.Core.Tests` · `build/publish.sh` or `build/publish.ps1` | [`.github/workflows/windows.yml`](.github/workflows/windows.yml) |
| 2 | macOS | `swift test` · `Scripts/make-app.sh --universal` | [`.github/workflows/macos.yml`](.github/workflows/macos.yml) |

Each platform workflow runs when its own files or shared release automation change, and publishes only its own tag prefix. Windows packages include a native macOS build; macOS packages include the latest verified Windows stable built from main.

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

Trusted Windows PRs publish optional beta previews such as `1.9.0-beta.23.11.1` before merge. Enable **Settings → Updates → Include beta releases** in a beta-aware Evict installation to receive these through the app.

See [automatic versioning](docs/versioning.md) for intent rules, scoped platform overrides, retry behavior and source provenance, and [beta updates](docs/beta-updates.md) for preview installation.

## Installation ZIP

Every final installation ZIP has exactly these top-level folders:

| Folder | Contains |
| --- | --- |
| `portable/` | Windows `Evict.exe` and its SHA-256 checksum |
| `windows-installer/` | `Evict-Setup-<WindowsVersion>.exe` and its SHA-256 checksum |
| `macOS/` | `Evict-macOS-<MacVersion>.zip` and its SHA-256 checksum |

The nested macOS archive contains the native app bundle and retains its executable permissions and framework symlinks. Each platform keeps its own version. No loose files or extra top-level folders are allowed. CI rejects missing builds, invalid archive contents or incorrect checksums.

The separate Windows installer and portable assets remain available for in-app updates. PR releases are always beta; normal releases publish after merge into `main`. Evict checks GitHub after each application restart when automatic checks are enabled; beta remains optional.

Actions downloads are named **Evict-installation** (Windows workflow) and **Evict-macOS-installation** (macOS workflow). Each contains the three folders above. The run summary shows the actual versions and download link. Successful Windows runs remove their temporary build transfers; see [build artifacts](docs/build-artifacts.md).

## History

This repository was `krishnabhunia/evict` (Windows only) and was renamed to `evict-uninstaller` on 29 Sep 2026; GitHub
redirects the old URL. The macOS app was imported from `krishnabhunia/evict-mac` with its full commit history
(its tag `v0.1.0` is `mac-v0.1.0` here).
