This is **IPKeep for Windows 1.0.0 Preview 3**, an unsigned beta with application update notifications. The desktop checks when it opens and every six hours while open. A newer release shows a banner; **App updates** displays the version, release date, and short notes in a read-only text box. **Download now** opens GitHub Releases. A manual **Check for updates** button is also available. Preview builds check both preview and stable releases. Downloads and installation remain manual; signing is still pending.

Download `IPKeep-1.0.0-preview.3-windows-x64-unsigned-setup.exe` and verify it against `SHA256SUMS.txt`. Run setup, approve Windows administrator access, and open IPKeep from the Start menu. Preview 2 and earlier require this upgrade manually to get future update notifications. This unsigned beta may trigger an unknown publisher or SmartScreen warning. Windows 10 version 2004 or later / Windows 11, x64, is required. The installer bundles the desktop, service, and runtimes. An IPKeep account and your own token are required to enable DNS updates; checking for app releases needs no token.

Setup preserves existing settings, encrypted tokens, and logs. Existing services are updated; paused/disabled services stay paused/disabled. Uninstall through Windows Installed apps to stop/remove the service and application while retaining local data for reinstallation. Installer integration tests run on a disposable Windows runner; interactive Windows 10/11 and reboot testing still need beta feedback.

The optional `IPKeep-1.0.0-preview.3-windows-x64-unsigned.zip` contains the complete folder distribution. Extract and keep the whole folder together. Both distributions include documentation and source commit metadata in `build-info.json`. Compiled app, metadata, and installer versions now consistently include the preview number.

The binaries are built from the tagged public source by GitHub Actions after the isolated tests pass. Opening the desktop performs public IP discovery and anonymous release checks. Choosing **Save and enable updates** with Windows administrator approval installs/enables the background service and begins authenticated DNS updates. Existing support for up to five hostnames, remembered tokens, provider selection, local-time activity, and retry backoff is retained.

- [Setup and removal instructions](https://github.com/calxibe/ipkeep-windows#readme)
- [Code signing policy](https://github.com/calxibe/ipkeep-windows/blob/main/CODE_SIGNING.md): we are preparing a SignPath Foundation application; this build is not signed or endorsed by SignPath Foundation.
- [Client privacy policy](https://github.com/calxibe/ipkeep-windows/blob/main/PRIVACY.md)
- [Source and MIT license](https://github.com/calxibe/ipkeep-windows)
