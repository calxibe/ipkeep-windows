# IPKeep for Windows privacy

This document describes the Windows client's current behavior. Hosted accounts and server-side data are covered by the [IPKeep service privacy policy](https://ipkeep.net/privacy).

## Network requests

Opening Overview looks up the public IP address using the selected provider, even before a token is entered. The default provider is IPKeep. Refreshing Overview repeats the lookup. Opening the provider comparison dropdown contacts all four listed providers: IPKeep, Amazon Check IP, ipify, and ident.me.

IP lookups send anonymous HTTPS requests. The destination can see the connection's public IP address and ordinary HTTP metadata, including the `IPKeep-Windows/1.0` user-agent. The client sends no account token, hostname, or update payload to external lookup providers.

The desktop checks `https://api.ipkeep.net/version?channel=stable` for application releases at startup and every six hours while open, or when you choose **Check for updates**. Preview builds additionally query `?channel=preview`. These anonymous requests include the app version in the user-agent; they send no token, hostname, cookies, or discovered IP addresses. The server can see the connection's public IP and ordinary HTTP metadata. Closing the desktop stops these checks.

Starting with Preview 7, newer installers are downloaded automatically while the desktop is open unless **Download updates automatically** is turned off. Downloads request the version's checksum file and installer from `github.com/calxibe/ipkeep-windows`; GitHub can redirect to `release-assets.githubusercontent.com`. These HTTPS requests include the application version and ordinary HTTP metadata, but no IPKeep token, hostname, cookies, or discovered IP addresses. GitHub sees the connection's public IP. Installation starts only when the user chooses **Install update** and accepts Windows setup. **Download from GitHub instead** opens the fixed GitHub Releases page in the browser.

When the user verifies a token or opens Settings with a remembered token, the client sends that token to `https://api.ipkeep.net/hosts` to retrieve the account's active hostnames. After the user saves and enables updates, the background service sends the token, selected hostname, and eligible public addresses to `https://api.ipkeep.net/update`. These are the normal updater operations. The optional diagnostics operations below also require an API token. Tokens are sent in the Authorization header over HTTPS, never in URLs.

The service runs independently of the desktop window and performs periodic checks until paused or removed. Pause updates stops those service checks; closing the desktop stops its foreground requests. IPKeep's own code contains no advertising, analytics, or automatic crash-report uploads. Bundled Microsoft runtimes and Windows may have their own diagnostics and data collection, governed by their settings, license terms, and the [Microsoft privacy statement](https://privacy.microsoft.com/privacystatement). Runtime license files and available notices are included under `third-party` in the download. Website links open the user's browser and are governed by the destination's policy.

Provider privacy information:

- [IPKeep](https://ipkeep.net/privacy)
- [Amazon Web Services](https://aws.amazon.com/privacy/)
- [ipify](https://www.ipify.org/) (see its logging statement)
- [ident.me](https://api.ident.me/#privacy--logging)

## Optional network diagnostics (Preview 11)

Diagnostic public-address discovery uses the IP lookup service currently selected in Settings. Its provider ID is included in the report so the address source can be identified later. An IPv4-only provider is not contacted for IPv6, and failures do not cause requests to a different provider.

Opening Diagnostics uses the token to read permitted-host service settings, this token's prior reports, and automatic Cloudflare/Google DNS comparisons through `https://api.ipkeep.net/diagnostics/`. Selecting **Test host** sends a timestamped, bounded snapshot: detected public IPv4/IPv6, the selected local addresses and ports, listener/binding state, local connection timing and web response status, Windows DNS answers, IPv4 gateway/ping result, updater-service state, router WAN addresses obtained through read-only UPnP/NAT-PMP (or a manual fallback), possible VPN/multiple-gateway indicators and a local IPv4 traceroute toward the selected lookup service (up to 12 numeric hops, statuses and timings). No interface names or adapter descriptions are uploaded. There is no automatic periodic local reporting. No process names, installed-software list, browser history, credentials, packet captures or response bodies are included.

IPKeep combines these client-reported observations with its regional measurements. Local connection checks target only this computer or the specific private LAN addresses and ports you select; no LAN-wide scan is performed. HTTP/HTTPS requests use the configured website hostname, verify TLS certificates, do not follow redirects, and send no API token to the local service. Gateway ping sends one ICMP check; a missing reply is inconclusive.

The hosted report and local snapshot expire after seven days. Deleting the host, account or originating API token erases the associated reports from the live database. Pausing or revoking a token denies API access and cancels queued work. Closing the dialog stops polling but a previously accepted regional job may finish. **Copy full report** places addresses and diagnostic details on your Windows clipboard only when clicked; review the report before sharing it.

## Local data

The desktop remembers a token using Windows DPAPI CurrentUser in `%LOCALAPPDATA%/IPKeep/Private/token.bin`. The service's separate connection is encrypted with DPAPI LocalMachine under `%ProgramData%/IPKeep/Private`, with restricted filesystem permissions. Tokens are decrypted in memory when used. The password field masks the restored value.

Per-host diagnostic mappings are stored in encrypted `diagnostic-*.bin` files beside the per-user token, bound to its hash and the hostname. They contain no plaintext token and remain on the computer after uninstall until this folder is removed. Manual WAN entries are not saved; older stored WAN values are ignored on load and removed on the next save.

Public settings, status, and activity logs contain hostnames and IP addresses. Logs also contain timestamps, provider names, HTTP status codes, timing information, and service events. Tokens and HTTP response bodies are not intentionally logged. Logs remain on the computer unless the user shares them, rotate around 2 MB, and retain five archives.

Verified installers and the automatic-download preference are stored in `%LOCALAPPDATA%/IPKeep/Updates`, restricted to the current Windows user and SYSTEM. These files contain no account token. Uninstalling retains this folder; deleting `%LOCALAPPDATA%/IPKeep` also removes it.

## Removal and contact

Removing the service stops background updates but retains settings, tokens, and logs. See [removal instructions](README.md#removing-ipkeep) for deleting retained local data. Revoke the token in the admin panel to disable its server access. Uninstalling the client does not delete the hosted account or hostnames.

For privacy or account questions, use [IPKeep support](https://apps.fixquotes.com/contact/?category=ipkeep).
