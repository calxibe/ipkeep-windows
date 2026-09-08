This is **IPKeep for Windows 1.0.0 Preview 2**, an unsigned beta with a Windows installer. It includes selection of up to five hostnames, remembered token/hostname choices, local-time activity logs, and improvements to background retry behavior and desktop refresh work. Signing and application auto-updates are not yet available.

Download `IPKeep-1.0.0-preview.2-windows-x64-unsigned-setup.exe` and verify it against `SHA256SUMS.txt`. Run setup, approve Windows administrator access, and open IPKeep from the Start menu. This unsigned beta may trigger an unknown publisher or SmartScreen warning. Windows 10 version 2004 or later / Windows 11, x64, is required. The installer bundles the desktop, service, and runtimes. An IPKeep account and your own token are required to enable DNS updates.

Setup preserves existing settings, encrypted tokens, and logs. Existing services are updated; paused/disabled services stay paused/disabled. Uninstall through Windows Installed apps to stop/remove the service and application while retaining local data for reinstallation. Installer integration tests run on a disposable Windows runner; interactive Windows 10/11 and reboot testing still need beta feedback.

The optional `IPKeep-1.0.0-windows-x64-unsigned.zip` contains the complete folder distribution. Extract and keep the whole folder together. Both distributions include documentation and source commit metadata in `build-info.json`.

The binaries are built from the tagged public source by GitHub Actions after the isolated tests pass. Opening the desktop performs public IP discovery. Choosing **Save and enable updates** with Windows administrator approval installs/enables the background service and begins authenticated DNS updates.

- [Setup and removal instructions](https://github.com/calxibe/ipkeep-windows#readme)
- [Code signing policy](https://github.com/calxibe/ipkeep-windows/blob/main/CODE_SIGNING.md): we are preparing a SignPath Foundation application; this build is not signed or endorsed by SignPath Foundation.
- [Client privacy policy](https://github.com/calxibe/ipkeep-windows/blob/main/PRIVACY.md)
- [Source and MIT license](https://github.com/calxibe/ipkeep-windows)
