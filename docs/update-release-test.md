# Installed update release test

These two Windows PRs exercise a stable update followed by an optional beta update. Their releases are published for testing while their PRs remain unmerged.

| Stage | Application version | Numeric Windows resource version | Channel |
| --- | --- | --- | --- |
| Current stable | 1.9.0 | 1.9.0.0 | Stable |
| First test PR | 1.10.0 | 1.10.0.0 | Stable |
| Second test PR | 1.11.0-beta.<PR>.<run>.<attempt> | 1.11.0.0 | Beta |

Stable versions always include all three components. Beta application labels additionally identify the PR, workflow run and attempt. The installer filename, installed registry version, application informational version and release tag use the full application version; Windows numeric resources use the numeric version.

## Explicit test release metadata

The first PR declares:

```text
Release-Type-Windows: minor
Release-Type-Macos: none
Release-Test-Windows: stable 1.10.0
```

The second declares:

```text
Release-Type-Windows: minor
Release-Type-Macos: none
Release-Test-Windows: beta 1.11.0
```

The test directive is an assertion about the version calculated by the release engine. Its committed source version must match. Only trusted same-repository Windows PRs can use it. Ordinary PR builds continue to use beta versions. Published assets and tags remain immutable.

The first test intentionally publishes to stable users before merge. The second requires beta opt-in. Windows and macOS maintain independent version tracks.

A published stable test version establishes a minimum version for the release engine in these PRs. Current main acquires that calculation change when the implementation is merged; its existing publication guard prevents a lower release from replacing a higher latest stable release.

## Verify in installed Evict

1. Start with the installed 1.9.0 stable release or the earlier beta-aware 1.9.0 beta.
2. Leave **Settings → Updates → Include beta releases** off and check for updates. Expect **1.10.0**.
3. Download and install the offered update. Confirm the running application shows **1.10.0** after restart.
4. After the second PR publishes, leave beta updates off and check again. Expect no 1.11.0 beta offer.
5. Enable **Include beta releases** and check again. Expect the complete **1.11.0-beta.<PR>.<run>.<attempt>** label and beta confirmation.
6. Cancel once and confirm the old version remains usable. Check again, accept the beta, and confirm the complete beta version after restart.
7. Check again. The same beta must not be offered as a newer update. Turning beta updates off must stop future beta offers without downgrading the installed app.

The update downloads the installer for installed Evict. It verifies the published SHA-256 checksum before handing off to Setup. The release ZIP contains only `installer/` and `portable/`, with their checksums.

## Automated evidence and limits

Unit scenarios cover stable upgrades, beta opt-out/opt-in, exact installer and checksum selection, verified downloads, installer arguments, corrupt payloads and cancellation. Test launches are captured without running the production app.

The explicit test-release verification job checks actual published release metadata, downloaded installer/portable/ZIP bytes and checksums, version resources and ZIP contents, then exercises the downloaded installer in isolated native fixtures. An opted-in live updater check uses the actual GitHub release and download path.

These checks prove the release and updater code paths in CI. The installed application's visible update banner, restart, retained settings and Bitdefender behavior must also be checked on the user's Windows machine. Keep the Setup log and antivirus detection details if installation fails.
