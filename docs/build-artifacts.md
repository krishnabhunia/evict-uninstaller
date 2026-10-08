# Build artifacts

Each successful Windows workflow keeps one final Actions download named **Evict-installation**. The macOS workflow keeps **Evict-macOS-installation**. Both download ZIPs contain exactly:

- `portable/`
- `windows-installer/`
- `macOS/`

The running application, Setup filename, release tag and release ZIP keep their full automatically calculated version. The workflow summary shows the actual Windows and Mac versions and links directly to the installation artifact. Mac workflow bundles use the published Windows stable from main.

## Temporary build transfers

Windows builds temporarily upload version metadata, Windows binaries, the native Mac archive, and publication inputs so separate jobs can exchange files. These can appear while the workflow is running.

After packaging and, when applicable, publication plus live update verification succeed, an isolated API-only cleanup job deletes those transfers. The final installation download remains. Fork and bot PRs do not receive cleanup write permissions.

Temporary artifacts use one-day retention. Failed packaging, publication or update verification preserves them for diagnosis. Final downloads follow the repository's normal artifact retention.

## Retries

The final artifact has a fixed readable name and uses overwrite on retries, so a successful rerun replaces the previous Actions download instead of adding another version-named download. GitHub Release tags and published asset bytes are preserved.

The version manifest consumer uses the producing job's output name rather than reconstructing a name from a later attempt.

## Existing successful builds

The cleanup migration checks only the known successful Windows runs `37663956345` (PR #14), `37663958842` (PR #13), and `37739868099` (main). It verifies the exact workflow, repository, latest successful attempt and one unambiguous final installation artifact before removing that run's internal transfers.

Their existing final downloads keep their original names. Other workflows, failed runs, unrelated artifacts, logs, releases and source branches are preserved. Missing or ambiguous collections are skipped; bounded pagination and identity checks finish before deletion.
