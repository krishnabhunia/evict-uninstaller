# Version picker (VP1)

Evict never moves to a version you did not pick. When GitHub has several newer versions — stable and beta — you see
all of them and choose the exact one. The same window rolls back to any older release.

## Screens

| SrNo. | Screen | What you see | Safety rule |
|---|---|---|---|
| 1 | Notice | Banner and header button: "3 newer versions found (1 stable, 2 beta)" → **Choose version…** | No one-click update |
| 2 | Updates tab | Every newer release, filter **All / Stable / Beta** with counts, tags *Newest* and *Latest stable* | Nothing is selected; the button stays grey until you pick |
| 3 | Confirm | From → To, the steps that will run, an amber note for a beta that skips a newer stable | Second check before anything changes |
| 4 | Version history & roll back tab | Every older release; *On this PC* or a download; *Your previous version* | Settings restore and pin offered |

Open it from the notice, the header button, or **Settings → Updates → Choose version… / Version history & roll back…**.

## What a change does

| SrNo. | Step | Notes |
|---|---|---|
| 1 | Save the current settings | `%LOCALAPPDATA%\Evict\versions\<version>\settings.json` |
| 2 | Keep the current version (ticked by default) | Portable: a copy of the running `Evict.exe`. Installed: the release's `Evict-Setup-<version>.exe`, downloaded and SHA-256 checked |
| 3 | Get the chosen version | A copy on this PC is used when its recorded SHA-256 still matches; otherwise the verified GitHub download |
| 4 | Roll back only: put back that version's settings | Default when a backup exists; this closing Evict then saves nothing more |
| 5 | Roll back only: pin (ticked by default) | `PinnedVersion` is set and the start-up check is turned off, so even versions without the picker stay quiet |
| 6 | Hand over | Same installer / exe swap as before; recorded in Software Updater history ("rolled back to …") |

The three most recently used versions are kept; the current and the target are never removed.

## Rules

| SrNo. | Rule |
|---|---|
| 1 | The notice counts only the chosen channel (stable, or stable + beta); the picker always lists both, filtered to Stable when betas are off |
| 2 | While pinned, nothing is offered; **Unpin** (or turning the start-up check back on) ends the pin. Choosing an update yourself also ends it |
| 3 | "Don't remind me about these" hides the notice until a version newer than the newest shown appears |
| 4 | A release without its file or SHA-256 for your edition is listed as *GitHub only* and cannot be installed by Evict |
| 5 | A roll back counts as done only when exactly that version starts |
| 6 | Versions released before VP1 do not know the picker; the pin still works for them because it turns the start-up check off |

## Tests

`windows/tests/Evict.Core.Tests/VersionPickerTests.cs` — lists, filter, notice text, tags, warnings, confirm steps, pin and
unpin, kept copies and checksum, settings restore with carried values, pruning, suspended saves, roll back history.
