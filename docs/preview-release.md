This is an **unsigned preview** of IPKeep for Windows 1.0.0, provided for evaluation and review of the open-source client. Signing, a full installer, and application auto-updates are not yet available. Service installation, reboot, upgrade/rollback, and removal still need broader testing on disposable Windows systems.

Download `IPKeep-1.0.0-windows-x64-unsigned.zip`, verify it against `SHA256SUMS.txt`, and extract the complete `IPKeep` folder. The archive includes the self-contained x64 desktop app, background service, documentation, and source commit in `build-info.json`. Keep the entire folder together. An IPKeep account and your own token are required to enable DNS updates.

The binaries are built from the tagged public source by GitHub Actions after the isolated tests pass. Opening the desktop performs public IP discovery. Choosing **Save and enable updates** with Windows administrator approval installs/enables the background service and begins authenticated DNS updates.

- [Setup and removal instructions](https://github.com/calxibe/ipkeep-windows#readme)
- [Code signing policy](https://github.com/calxibe/ipkeep-windows/blob/main/CODE_SIGNING.md): we are preparing a SignPath Foundation application; this build is not signed or endorsed by SignPath Foundation.
- [Client privacy policy](https://github.com/calxibe/ipkeep-windows/blob/main/PRIVACY.md)
- [Source and MIT license](https://github.com/calxibe/ipkeep-windows)
