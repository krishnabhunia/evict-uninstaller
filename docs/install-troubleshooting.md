# Installation diagnostics

Setup creates a log automatically. If installation stops while extracting or copying files, retain the complete error message and the path named in it.

1. Exit Evict from its notification-area menu before installing a replacement.
2. Download the installer and its matching `.sha256` from the same GitHub release. Compare the SHA-256 using `Get-FileHash -Algorithm SHA256`.
3. Run Setup again and retain the latest `Setup Log` in your Windows temporary folder. A specific log path can be requested with `/LOG="C:\\path\\evict-setup.log"`.
4. In Bitdefender, check Notifications and **Protection → Antivirus → Open → Settings → Manage quarantine** for a detection at the same time and for the exact setup, temporary or installed file path.

An antivirus detection is evidence that a file was blocked. An extraction or copying error alone can also result from a corrupt download, permissions, disk space or an executable still in use.

Evict's installer requests the running app to exit through its existing local IPC protocol and waits for it to finish. It does not extract and launch a second copy merely to request exit. Silent installs and updates stop before copying when Evict remains running. Close the existing app and retry.

A beta may be unsigned if signing credentials are not configured. If Bitdefender reports a suspected false positive, submit the exact detected release file and detection details to Bitdefender for analysis. Keep the release version, file checksum and installer log with the report.

References: [Inno Setup logging](https://jrsoftware.org/ishelp/topic_setup_setuplogging.htm), [Bitdefender quarantine details](https://www.bitdefender.com/consumer/support/answer/2092/).
