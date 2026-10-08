# Automatic release versions

Evict uses `x.y.z` and GitHub Actions calculates the version from the change. Contributors do not edit version numbers or create release tags manually.

| Component | Meaning | Example from 1.9.0 |
| --- | --- | --- |
| x | Major or breaking update | 2.0.0 |
| y | Minor update or new feature | 1.10.0 |
| z | Bug or error fix | 1.9.1 |

A major increment resets minor and patch; a minor increment resets patch. The highest required increment among unreleased Windows or macOS changes determines the suite version, which matches the Windows application's `win-v` version. The native macOS component keeps its independent numeric `x.y.z` and bundle build number; verified stable delivery records its `mac-v` component tag in the same workflow.

## Declare the change

| Change | PR title or commit | Description or label |
| --- | --- | --- |
| Major | `feat!: change the public behavior` | `Release-Type: major` or `release:major` |
| Minor | `feat: add startup update checks` | `Release-Type: minor` or `release:minor` |
| Patch | `fix: handle interrupted downloads` | `Release-Type: patch` or `release:patch` |
| No release | Documentation or tests only | `Release-Type: none` or `release:none` |

Per-platform declarations such as `Release-Type-Windows: minor` and `Release-Type-Macos: none` override general declarations for that platform. The bundle uses the highest resulting platform increment, so a macOS-only feature also publishes a new suite download. A breaking marker cannot be reduced by a smaller declaration. Unclassified changes to application or delivery code receive a patch increment.

## PR beta delivery

Every PR package uses the automatically calculated next core plus `-beta.<run>.<PR>.<attempt>`. The globally increasing unified workflow run is first, so a later build of an older PR remains newer than earlier builds from other PRs. Historical PR-first beta tags still parse correctly and remain immutable.

GitHub Actions synchronizes the managed Windows version, installer metadata and changelog into the frozen beta source before building. The only allowed generated differences from the reviewed app source are those managed version files. The final tested source SHA and original PR head are recorded in the release notes. The workflow subtree matches the default branch so publication works with the normal GitHub token.

Trusted same-repository PRs publish GitHub prereleases with `make_latest: false`. Fork and bot PRs validate with read-only permissions and do not publish. **No PR can publish a stable release**, including a test PR. `Release-Test-Windows: beta` optionally requests the live published-installer verification; it does not set a manual version.

## Stable delivery after merge

When the PR is merged into `main`, GitHub Actions:

1. Fetches current main history and platform tags.
2. Calculates the required major, minor or patch increment.
3. Synchronizes the managed source version, installer or app metadata, and changelog.
4. Pushes the generated version commit using a compare-and-swap retry if main advances.
5. Tests and packages that exact source, then publishes one normal `x.y.z` suite release with Windows and macOS payloads.

Stable planning requires the exact current `origin/main` source. Stable tag builds must resolve to a source reachable from main. A final release sorts above its beta at the same core. GitHub does not trigger another push workflow for a commit written with `GITHUB_TOKEN`; the workflow continues with its generated source.

Published tags and asset bytes stay immutable. Retry a failed main delivery through Actions. A higher already shipped Windows core is never reused or silently downgraded.

## Historical reservation

The old `win-v1.10.0` stable test was published from unmerged PR #12. It is withdrawn into a draft so it cannot appear in the stable updater channel. Its exact tag and bytes remain reserved. The migration trusts only its pinned repository, tag, frozen source, original reviewed source and matching app tree; arbitrary unreachable tags and draft releases cannot reserve new versions.

This one historical reservation prevents reuse of 1.10.0. After PR #12 was merged, main automatically released 1.11.0. Future versions are calculated from the newest published main release and the declared change; for example, a minor update from 1.11.0 becomes 1.12.0 in beta and then stable.

## Installation archive and startup checks

The single **Evict Build & Release** workflow produces `Evict_<fullversion>.zip`. Its only enclosing folder is `Evict_<fullversion>/`, containing exactly `portable/Evict_<fullversion>.exe`, `windows-x64/Evict_<fullversion>.exe` and `macOS/Evict_<fullversion>.dmg`. There are no internal checksum sidecars or nested ZIPs. The DMG preserves native Mac app metadata and permissions. The macOS component version remains independent; all download filenames use the complete suite version. Release checksums and raw Windows updater assets remain separate.

Evict checks GitHub silently after every application restart when automatic checks are enabled. A saved last-check time does not suppress a new startup check. Stable is the default; beta requires **Settings → Updates → Include beta releases**. Shutdown cancels pending work, and a manual check replaces the delayed startup request.

See [installed update testing](update-release-test.md) and [beta updates](beta-updates.md).
