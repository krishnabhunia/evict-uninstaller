# Start-up splash screen (design SP1, Evict 4.1)

Design canvas: <https://claude.ai/artifact/CnCkrBCMzXy6bqGBpgA1Sj> — built: **S1 "brand card"** with its three states.

## What happens when you open Evict

| # | Step | Splash shows |
|---|---|---|
| 1 | Splash appears before anything else loads (own UI thread, started in `Program.Main`) | "Starting…" |
| 2 | Administrator prompt, when *Start as administrator* is on | "Waiting for administrator permission…" (no bar) |
| 3 | Settings, theme | "Reading your settings…" · 1 of 4 |
| 4 | Uninstall and update history | "Reading your history…" · 2 of 4 |
| 5 | Installed programs (the list Apps and Home need) | "Loading your programs…" · 3 of 4 |
| 6 | Main window drawn hidden (DWM cloak), shown after its first frame; splash fades out | "Opening the window…" · 4 of 4 |

Health scan, folder sizes and the update check carry on in the open window.

## Rules

| Case | Behaviour |
|---|---|
| Tray / sign-in / scheduled-scan start, Setup's `--exit`, self-cleanup | No splash (`StartupSplashRules.ShouldShow`) |
| Programs take longer than 8 s | "Taking longer than usual" + **Open now** |
| 20 s | The window opens anyway; the list fills in |
| An exception while preparing | "Something failed" + the first line of the error, **Open Evict** / **Show log** |
| Second launch | Forwarded to the running Evict; no second splash |
| Arguments (`--uninstall-file`, `--page`, widget) | Run after the window is up (dialogs need a shown owner) |
| The cloak call fails or the first frame never arrives | The window is uncloaked after 3 s anyway |

## Files

| Part | File |
|---|---|
| Rules and texts | `windows/src/Evict.Core/Util/StartupSplashRules.cs` |
| Splash window (code only, fixed colours) | `windows/src/Evict.App/Views/SplashWindow.cs` |
| Splash thread | `windows/src/Evict.App/Services/StartupSplash.cs` |
| Start-up order | `Program.cs`, `App.xaml.cs` (`ShowWhenReadyAsync`) |
| Hidden first draw | `MainWindow.xaml.cs` (`StartCloaked`, `Revealed`, `EnsureRevealed`) |
| Tests | `windows/tests/Evict.Core.Tests/StartupSplashTests.cs` |
