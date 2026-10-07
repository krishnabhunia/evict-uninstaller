# Beta updates

Evict's preferred update flow uses GitHub Releases. Stable releases are selected by default. The optional **Include beta releases** setting allows a newer Windows beta to appear in the usual update banner and release-notes dialog. The user chooses whether to download and install it; a beta needs an additional confirmation.

## First installation

Installed Evict 1.8.0 only understands stable updates. It cannot acquire a beta setting from release metadata alone. Install a beta-aware build once, then enable the option in Settings → Updates. Subsequent beta builds can be downloaded through Evict without merging their PR into main.

For manual testing, download the Windows Actions artifact or the published beta installer. Exit an already running Evict first so its single-instance forwarding does not reopen the old binary. Beta and stable installations use the existing Evict installation and data profile.

## Version and channel rules

- PR packages use the automatically calculated next major, minor or patch version with a suffix, for example `1.9.0-beta.<run>.<PR>.<attempt>`. See [automatic versioning](versioning.md).
- The running application's informational version includes the suffix; installers and update dialogs display it.
- Beta identifiers are compared numerically, so beta.10 is newer than beta.9.
- A final release is newer than its beta at the same numeric version.
- Opting out removes pending beta offers. It does not automatically downgrade a beta that is already installed.
- Release selection ignores drafts and macOS tags, follows the complete supported release-list pagination, and stops if it cannot verify that list.
- Both installer and portable updates require their published SHA-256 checksum. No update runs after an invalid or missing checksum.

## Publication and provenance

Trusted same-repository Windows PR builds test and package a beta before publishing a GitHub prerelease. Fork and Dependabot PRs can build with read-only access and do not publish releases. Only main-branch sources publish stable releases after merge, using the same automatic major/minor/patch calculation. No test PR can bypass this channel rule.

GitHub requires workflow-write permission when a release target changes workflows relative to the default branch. The publisher creates a frozen source snapshot preserving the reviewed PR app, tests and documentation, except for the three explicitly managed Windows version files synchronized by the version engine. The workflow subtree matches the default branch. The snapshot's parent is the default-branch commit. Its scoped build reference makes that snapshot available for checkout. This publishes app code with the normal contents-write token while keeping the default branch unchanged.

The original PR head and the tested snapshot SHA are recorded in the release notes. Workflow-subtree and reviewed-source checks verify that all other non-workflow blobs stay unchanged before building. Release assets and their hashes are checked before the draft is published as a prerelease with `make_latest: false`. No merge is performed.

## Startup checks

Automatic checks run silently after every application restart when enabled, even if the settings remember a recent check. Stable releases are the default. Saved beta opt-in controls the startup channel. Manual and startup checks share one request; shutdown cancels pending work.

## Validation

Automated tests cover stable-default settings, persisted beta opt-in, semantic version ordering, release eligibility, release-list failures and pagination, and verified downloads. Windows CI compiles the app, checks XAML, tests the beta build, builds the native macOS bundle, and packages exactly `portable/`, `windows-installer/` and `macOS/`. All binaries and nested app archives have SHA-256 sidecars.

After installing the beta-aware build, use disposable installations to check:

1. Beta updates are absent while the option is off.
2. Enabling the option and checking updates offers a newer published beta with its complete label and release notes.
3. Cancelling beta confirmation or download leaves the current installation running.
4. Accepting the update verifies the checksum, installs the appropriate asset and restarts the new build.
5. A later beta is offered; the same beta and older builds are not.
6. Disabling the option removes a pending beta offer; a later stable release remains eligible.
