# Software Updater history

Open **Software Updater → Update history**. Available updates, exclusions, CSV export and Evict's own update records remain available.

- Result filters include Updated, Failed, Cancelled, Interrupted and Restart required. Selecting a row shows its version change, times, source, result and bounded output; Copy details includes this information.
- Completed attempts expire after a rolling 168 hours measured from completion in UTC. Cleanup runs at startup, once per minute and on Refresh history. Active attempts do not expire.
- Queued and running attempts are journalled before installation. On restart, unfinished queued attempts become Cancelled, while started installers with no confirmed result become Interrupted. Recovery is persisted once.
- Cancelling a batch requires confirmation. Queued packages are recorded as cancelled; already running installers have an unknown outcome. Installer timeouts are also interrupted, even if partial output says success.
- **Check latest update** rechecks the package identifier and source, respects exclusions and selects the current eligible update for review. It does not install a stale version automatically.
- **History settings** explains local retention and the recorded fields. Clearing completed history requires confirmation and preserves active attempts, installed programs, exclusions and self-update metadata.
- Read/write failures have a visible banner with Retry. New records remain in memory when persistence fails. Unreadable or unsupported files are preserved until they can be recovered; retry merges pending records. Failed clear operations retain the displayed records.

Storage uses the existing `%LocalAppData%\Evict\update-history.json`, keeping prior finished records and self-update metadata. Writes use a unique temporary file, disk flush and atomic replacement. The journal remains separate from uninstall history and is classified as history by Evict's self-cleanup tool.

The approved HTML preview covers the overview, available updates, progress, completion, success/failure/restart/cancelled/interrupted details, cancel/clear confirmations, empty and filtered states, read/write failures, settings and exclusions. These behaviors use Evict's existing WPF light/dark theme; confirmation uses the standard Windows dialog.
