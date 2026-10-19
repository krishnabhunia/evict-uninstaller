# Software Updater history

Open **Software Updater → Update history**. This history covers program updates attempted through Evict, separately from uninstall history and Evict's own GitHub release channel.

- Each selected program has its own record: package ID/source, previous and requested version, result, start/end times, exit code, and a bounded installer log.
- Results include Updated, Failed, Restart required, Cancelled, Interrupted and In progress. A nonzero installer failure is not converted into success by a matching output phrase.
- Completed attempts are retained for a rolling 168 hours using UTC timestamps. Cleanup runs at application startup, once a minute while Evict is open, and before history is refreshed. Active attempts are kept until a result is recorded.
- An unfinished attempt from a previous Evict process becomes Interrupted, with an unknown installer outcome. Cancellation of queued work is Cancelled; cancellation while the installer was running remains Interrupted unless a final result was actually reported.
- **View** opens details and the captured activity log. **Copy details** copies the selected attempt. Failed, cancelled and interrupted attempts can **Check for latest update**, which returns to available updates and selects only the matching package/source. It does not launch an old installer or restore an exclusion automatically.
- **History settings** shows the fixed seven-day policy. **Clear history** requires confirmation and removes only completed attempts; active updates, installed programs and exclusions are preserved.
- The history is stored locally at the existing Evict data folder as software-update-history.json. Each save replaces a complete flushed journal. Unreadable or unsupported files are not overwritten. Read/write failures remain visible, pending records stay in memory, and Retry merges recovered records with pending attempts. Keep Evict open while a save failure is unresolved.
- No older system-wide update history is imported. Only attempts made after installing a version with this feature can be recorded.

The approved HTML design is implemented using Evict's existing WPF theme and owned dialogs. Confirmation uses Evict's standard native Windows confirmation dialog. Times are shown in the current Windows timezone with offsets in the details view.

Regression coverage includes the exact retention boundary, timezone offsets, restart recovery, failed reads/writes, atomic replacement, cancellation before/after launch, parallel execution, independent attempt results and installer exit-code classification.
