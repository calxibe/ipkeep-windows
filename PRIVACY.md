# IPKeep for Windows privacy

This document describes the Windows client's current behavior. Hosted accounts and server-side data are covered by the [IPKeep service privacy policy](https://ipkeep.net/privacy).

## Network requests

Opening Overview looks up the public IP address using the selected provider, even before a token is entered. The default provider is IPKeep. Refreshing Overview repeats the lookup. Opening the provider comparison dropdown contacts all four listed providers: IPKeep, Amazon Check IP, ipify, and ident.me.

IP lookups send anonymous HTTPS requests. The destination can see the connection's public IP address and ordinary HTTP metadata, including the `IPKeep-Windows/1.0` user-agent. The client sends no account token, hostname, or update payload to external lookup providers.

The desktop also checks `https://api.ipkeep.net/version` for application releases at startup and every six hours while open, or when you choose **Check for updates**. Preview builds additionally query `?channel=preview`. These anonymous requests include the app version in the user-agent; they send no token, hostname, cookies, or discovered IP addresses. The server can see the connection's public IP and ordinary HTTP metadata. Closing the desktop stops these checks. **Download now** opens the fixed GitHub Releases page in your browser; the app does not automatically download or install packages.

When the user verifies a token or opens Settings with a remembered token, the client sends that token to `https://api.ipkeep.net/hosts` to retrieve the account's active hostnames. After the user saves and enables updates, the background service sends the token, selected hostname, and eligible public addresses to `https://api.ipkeep.net/update`. These are the only authenticated operations. Tokens are sent in the Authorization header over HTTPS, never in URLs.

The service runs independently of the desktop window and performs periodic checks until paused or removed. Pause updates stops those service checks; closing the desktop stops its foreground requests. IPKeep's own code contains no advertising, analytics, or automatic crash-report uploads. Bundled Microsoft runtimes and Windows may have their own diagnostics and data collection, governed by their settings, license terms, and the [Microsoft privacy statement](https://privacy.microsoft.com/privacystatement). Runtime license files and available notices are included under `third-party` in the download. Website links open the user's browser and are governed by the destination's policy.

Provider privacy information:

- [IPKeep](https://ipkeep.net/privacy)
- [Amazon Web Services](https://aws.amazon.com/privacy/)
- [ipify](https://www.ipify.org/) (see its logging statement)
- [ident.me](https://api.ident.me/#privacy--logging)

## Local data

The desktop remembers a token using Windows DPAPI CurrentUser in `%LOCALAPPDATA%/IPKeep/Private/token.bin`. The service's separate connection is encrypted with DPAPI LocalMachine under `%ProgramData%/IPKeep/Private`, with restricted filesystem permissions. Tokens are decrypted in memory when used. The password field masks the restored value.

Public settings, status, and activity logs contain hostnames and IP addresses. Logs also contain timestamps, provider names, HTTP status codes, timing information, and service events. Tokens and HTTP response bodies are not intentionally logged. Logs remain on the computer unless the user shares them, rotate around 2 MB, and retain five archives.

## Removal and contact

Removing the service stops background updates but retains settings, tokens, and logs. See [removal instructions](README.md#removing-ipkeep) for deleting retained local data. Revoke the token in the admin panel to disable its server access. Uninstalling the client does not delete the hosted account or hostnames.

For privacy or account questions, use [IPKeep support](https://fixquotes.com/docs/contact/?category=ipkeep).
