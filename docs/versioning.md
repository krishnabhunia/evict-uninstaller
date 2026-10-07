# Automatic release versions

Evict maintains stable versions as `x.y.z`. GitHub Actions calculates and records the version; contributors do not edit version numbers by hand.

| Change | PR title or commit example | From 1.8.0 |
| --- | --- | --- |
| Major update / breaking change | `feat!: redesign the uninstall workflow` | 2.0.0 |
| New feature | `feat: add an update channel selector` | 1.9.0 |
| Bug or error fix | `fix: handle interrupted downloads` | 1.8.1 |

Use these titles for pull requests and Conventional Commit messages for direct commits. The highest required increment among unreleased changes wins. A major increment resets the minor and patch numbers; a minor increment resets the patch number.

A PR can also declare its release type in its description or with a release label:

| Metadata | Examples |
| --- | --- |
| Description line | `Release-Type: major`, `Release-Type: minor`, `Release-Type: patch`, `Release-Type: none` |
| Label | `release:major`, `release:minor`, `release:patch`, `release:none` |
| Per-platform description | `Release-Type-Windows: minor`, `Release-Type-Macos: patch` |
| Per-platform label | `release:windows:minor`, `release:macos:patch` |

An explicit platform declaration overrides the general declaration or title for that platform. A breaking marker always requires a major increment. Multiple declarations at the same level use the highest increment. `none` is available for changes that deliberately do not require delivery.

Documentation and test changes alone do not publish an application release. Unclassified changes to application or delivery code receive a patch increment.

Windows and macOS keep independent version tracks, identified by `win-v` and `mac-v` tags. A macOS release never replaces the latest Windows release.

## Stable delivery

After a change reaches `main`, the platform workflow:

1. Fetches the latest main history and stable platform tags.
2. Calculates the version from the changes since that platform's last stable tag, ignoring beta tags and automatic version commits.
3. Updates the managed source version, installer or app metadata, and changelog.
4. Pushes a version commit without force. If main advances, it fetches and recalculates instead of overwriting changes.
5. Tests and packages that exact commit, then publishes its matching release tag.

Every PR validation package has a beta suffix, including documentation-only builds that do not publish a prerelease. It cannot masquerade as a future stable release.

The same workflow continues after committing the version. GitHub does not start another push workflow for a commit made with `GITHUB_TOKEN`.

Failed delivery can be retried with **Actions → Windows or macOS → Run workflow → main**. Published releases are not replaced with newly built files. Tag builds verify that the source version matches the tag.

## PR previews

A trusted Windows PR calculates the same next core version before merge and adds a beta suffix, for example `1.9.0-beta.11.23.1`. The suffix identifies the PR, workflow run and attempt. Stable releases retain the exact `x.y.z` format.

The installed app finds these previews when **Settings → Updates → Include beta releases** is enabled. The PR stays open while its features are tested. See [beta updates](beta-updates.md) for download and source verification details.

## Migration

PR #10 added the Windows beta update feature and fixed macOS behavior before release metadata existed. The policy records a Windows minor increment and a macOS patch increment for that PR. This avoids misclassifying its neutral title and requires no manual version edits.

## References

- [Semantic Versioning](https://semver.org/)
- [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/)
- [GitHub token workflow behavior](https://docs.github.com/en/actions/concepts/security/github_token)
