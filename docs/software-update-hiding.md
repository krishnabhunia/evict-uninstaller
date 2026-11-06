# Software Updater: hidden updates

Some winget offers never go away. Example: Internet Download Manager reports **6.43.18**, winget offers **6.43.18.1**.
The extra `.1` is winget's revision for a re-uploaded installer of the *same* build, so after updating the program
still reports 6.43.18 and the offer returns on every check. Evict handles this for **every** package with the same
generic rules (design VM1, option B). Nothing is keyed to a particular program.

## Rules

| SrNo. | Rule | Example | Result | Comes back when |
|---|---|---|---|---|
| 1 | Unchanged after updating: winget reported success, "already installed" or "not newer", yet the next check offers exactly the same installed and available versions | IDM still 6.43.18 after the update | Hidden as *Already up to date*; a notice offers **Undo** | winget offers another version, or the installed version changes |
| 2 | Same version written differently (trailing `.0` parts, a leading `v`, spaces ignored) | `1.2` vs `1.2.0.0` | Hidden as *Same version* | the versions stop being equal |
| 3 | Same build repackaged: the available version is the installed version (three or more numbers) plus exactly one extra non-zero number | `6.43.18` → `6.43.18.1` | Hidden as *Same build, repackaged* | an earlier number changes |
| 4 | **Skip this version** (per row, or *Skip selected version*) | not ready for 8.7.1 | Hidden for that available version only | a newer version is offered |
| 5 | **Exclude selected** (existing) | never update this program | Hidden for every version | restored by you |

Not treated as mismatches, so still listed: `6.43 → 6.43.1` (a patch release of a two-number version),
`1.2.3.0 → 1.2.3.1` (same length), versions winget cannot read (`Unknown`, `< 1.0`, `1.2-beta`).
A restart-pending update is never judged as unchanged.

## Where the filter applies

1. Software Updater list, its counts and summary line.
2. Software Health *Outdated programs* tile, and therefore the tray and scheduled-scan summaries. Exclusions now apply here too.
3. Update history keeps the attempt, with the message "No change: Windows still reports … after the update".

## Hidden updates window

**Software Updater → Hidden updates (n)** lists every hidden offer with SrNo., versions, why it is hidden and when it
comes back, filterable by *Already up to date*, *Skipped versions* and *Excluded*. **Restore** lists the exact offer
again (automatic rules are overridden for those versions only); restoring a skip or an exclusion removes it.

## Settings

**Settings → Updates → Software Updater**

| SrNo. | Setting | Default |
|---|---|---|
| 1 | Hide updates that don't change the installed version (rules 1 and 2) | On |
| 2 | Treat repackaged builds as up to date (rule 3) | On |

Decisions are saved in `settings.json` (`HiddenSoftwareUpdates`) per package ID + source + versions and are removed
automatically when winget offers other versions for that package.
