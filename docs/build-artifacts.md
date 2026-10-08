# Build artifacts

**Evict Build & Release** is the single active workflow for Windows and macOS. Its definition is [`windows.yml`](../.github/workflows/windows.yml); it runs native Windows and Mac jobs from the same release source.

Each successful run keeps one Actions download named **`Evict_<fullversion>`**. GitHub downloads it as **`Evict_<fullversion>.zip`**. The ZIP has one enclosing folder and exactly these three payload files:

| Path inside the ZIP | Native build |
| --- | --- |
| `Evict_<fullversion>/portable/Evict_<fullversion>.exe` | Windows portable application |
| `Evict_<fullversion>/windows-x64/Evict_<fullversion>.exe` | Windows x64 installer |
| `Evict_<fullversion>/macOS/Evict_<fullversion>.dmg` | Universal macOS disk image |

The full version includes the beta suffix for a PR build. The macOS app inside the disk image retains its independently managed numeric component version. The run summary shows both versions and links to the final download.

The release installation ZIP uses the same name and layout. SHA-256 checksums are separate release assets; no sidecars or nested archives are added to the installation ZIP. Raw `Evict.exe` and `Evict-Setup-<fullversion>.exe` updater assets remain available with their checksums.

## Temporary build transfers

Jobs temporarily upload version metadata, Windows binaries, the native Mac build and publication inputs. These can appear while a run is active.

After packaging and, when required, publication plus live update verification succeed, the cleanup job removes internal transfers and previous-attempt final downloads owned by that run. The current `Evict_<fullversion>` download remains. Temporary transfers expire after one day if the run fails. Fork and bot PRs do not receive cleanup write permissions.

Final downloads follow the repository's normal artifact retention. Historical workflow runs and their surviving final downloads keep their original names; removing the separate macOS workflow does not erase its history.
