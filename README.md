# Evict Uninstaller

Uninstaller and leftover cleaner for **Windows** and **macOS**. The two apps are separate native programs
(no shared code). This repository holds both.

| SrNo. | Platform | Folder | Stack | Version source | Release tags | Docs |
|---|---|---|---|---|---|---|
| 1 | Windows 10/11 | [`windows/`](windows/) | C# / .NET 8 / WPF, Inno Setup | [Build metadata](windows/Directory.Build.props) | `win-vX.Y.Z` (`vX.Y.Z` up to 1.3.3) | [windows/README.md](windows/README.md) |
| 2 | macOS 14+ | [`macos/`](macos/) | Swift 5.9 / SwiftUI, SwiftPM | [Version.swift](macos/Sources/EvictKit/Version.swift) | `mac-vX.Y.Z` component tags | [macos/README.md](macos/README.md) |

## Downloads

[Releases](https://github.com/krishnabhunia/evict-uninstaller/releases): download `Evict_<fullversion>.zip` for both platforms, or choose the separate `Evict.exe` / `Evict-Setup-x.y.z.exe` Windows updater assets. Only Windows releases are marked *Latest*, because installed Windows copies
up to 1.3.3 update themselves from `releases/latest`.

## Building

| SrNo. | Platform | Commands (run inside the folder) | CI workflow |
|---|---|---|---|
| 1 | Windows | `dotnet test tests/Evict.Core.Tests` · `build/publish.sh` or `build/publish.ps1` | [`.github/workflows/windows.yml`](.github/workflows/windows.yml) |
| 2 | macOS | `swift test` · `Scripts/make-app.sh --universal` | [`.github/workflows/windows.yml`](.github/workflows/windows.yml) |

**Evict Build & Release** is the single active workflow. Native Windows and macOS jobs build the same release source, then produce one combined installation download. Historical runs of the removed macOS workflow remain visible in Actions.

## Automatic versioning and releases

GitHub Actions maintains versions as **x.y.z** and records the calculated version in source before testing and packaging the release.

| Change | PR title or commit example | From 1.8.0 |
| --- | --- | --- |
| Major update / breaking change | `feat!: redesign the uninstall workflow` | 2.0.0 |
| New feature | `feat: add a beta update channel` | 1.9.0 |
| Bug or error fix | `fix: handle interrupted downloads` | 1.8.1 |

The highest Windows or macOS increment among unreleased changes determines the suite release. Major increments reset minor and patch; minor increments reset patch. The suite version matches the Windows application version; the native macOS component keeps its independently managed numeric version.

Use the PR title, a `release:major` / `release:minor` / `release:patch` label, or a `Release-Type:` description line to describe the change. Contributors do not need to edit version files or create version tags. Documentation and test changes alone do not publish a release.

After a merge to `main`, the workflow calculates the versions, commits the managed version files and changelogs, tests that exact source commit, and publishes one stable suite release with both native builds. Published releases stay unchanged; failed publication can be retried from Actions on `main`.

Trusted Windows PRs publish optional beta previews such as `1.9.0-beta.23.11.1` before merge. Enable **Settings → Updates → Include beta releases** in a beta-aware Evict installation to receive these through the app.

See [automatic versioning](docs/versioning.md) for intent rules, scoped platform overrides, retry behavior and source provenance, and [beta updates](docs/beta-updates.md) for preview installation.

## Installation ZIP

Actions downloads and release ZIPs are named **`Evict_<fullversion>.zip`**. Each ZIP has one enclosing `Evict_<fullversion>/` folder containing exactly:

| Folder | Contains |
| --- | --- |
| `portable/` | `Evict_<fullversion>.exe` — Windows portable app |
| `windows-x64/` | `Evict_<fullversion>.exe` — Windows x64 installer |
| `macOS/` | `Evict_<fullversion>.dmg` — universal Mac app |

The version includes its complete beta suffix for a PR build. There are exactly three native payload files and no checksum sidecars inside the installation ZIP. The DMG preserves the Mac app's executable permissions and bundle symlinks. CI rejects missing builds and invalid archive contents.

Separate Windows updater assets and SHA-256 checksums remain available on the release. PR releases are always beta; normal releases publish after merge into `main`. Evict checks GitHub after each application restart when automatic checks are enabled; beta remains optional.

Successful runs retain one version-named Actions artifact and remove temporary build transfers. The run summary shows the suite and native Mac versions plus the download link. See [build artifacts](docs/build-artifacts.md).

## History

This repository was `krishnabhunia/evict` (Windows only) and was renamed to `evict-uninstaller` on 29 Sep 2026; GitHub
redirects the old URL. The macOS app was imported from `krishnabhunia/evict-mac` with its full commit history
(its tag `v0.1.0` is `mac-v0.1.0` here).
