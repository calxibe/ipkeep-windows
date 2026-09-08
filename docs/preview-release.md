This is **IPKeep for Windows 1.0.0 Preview 4**, an unsigned beta with automatic light/dark appearance and clearer hostname selection.

- Follows Windows app light or dark mode on launch and while the window stays open. If the Windows preference is unavailable, IPKeep uses light mode.
- Dark surfaces, readable text and controls, matching navigation and title bar, and the reversed IPKeep logo. High contrast uses Windows system colors.
- Activity rows highlight across their width when hovered, while retaining compact local timestamps, wrapping, filtering, and selectable text.
- Fixes faint hostname checkboxes: selected hosts have a blue fill and contrasting checkmark; unchecked boxes have a visible outline. Supports up to five hostnames as before.

Download `IPKeep-1.0.0-preview.4-windows-x64-unsigned-setup.exe` and verify it against `SHA256SUMS.txt`. Run setup, approve Windows administrator access, and open IPKeep from the Start menu. Preview 3 can notify you about this release under **App updates**; earlier versions need a manual download. This unsigned beta may trigger an unknown publisher or SmartScreen warning. Windows 10 version 2004 or later / Windows 11, x64, is required. The installer bundles the desktop, service, and runtimes.

Setup preserves settings, encrypted tokens, and logs. It stops an existing service before replacing its active files, resumes a previously running service, and preserves paused/disabled states. Uninstall through Windows Installed apps to remove the service and application while retaining local data. Downloads and installation remain manual; code signing is pending.

The optional `IPKeep-1.0.0-preview.4-windows-x64-unsigned.zip` contains the complete folder distribution. Keep its files together. Both distributions include documentation and source/version metadata in `build-info.json`.

The tagged source is built by GitHub Actions after 69 isolated tests and eight installer integration checks on a disposable Windows runner. Light/dark layouts, open-dropdown theme switching, blue hostname checkboxes, Activity row hover, and the unavailable-preference fallback were inspected in the native app using isolated fictional UI data. No installed service or saved connection was changed by those visual checks. Interactive Windows 10/11 and reboot testing still benefit from beta feedback.

Opening the desktop performs public IP discovery and anonymous release checks. DNS updates require your IPKeep account token, hostname selections, and **Save and enable updates** with administrator approval. Existing encrypted token restoration, provider selection, retry backoff, and app update notifications are retained.

- [Setup and removal instructions](https://github.com/calxibe/ipkeep-windows#readme)
- [Code signing policy](https://github.com/calxibe/ipkeep-windows/blob/main/CODE_SIGNING.md): this build is not signed or endorsed by SignPath Foundation.
- [Client privacy policy](https://github.com/calxibe/ipkeep-windows/blob/main/PRIVACY.md)
- [Source and MIT license](https://github.com/calxibe/ipkeep-windows)
