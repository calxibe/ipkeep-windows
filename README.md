# IPKeep for Windows

A native WinUI 3 app and Windows background service that keep your [IPKeep](https://ipkeep.net/) dynamic DNS hostnames updated automatically. Connect with your own IPKeep API token, choose an existing hostname, and let the service check your public IP in the background.

**Version 1.0.0** · Windows 10 version 2004 or later / Windows 11 · x64 · [MIT license](LICENSE)

This repository contains the Windows client and service. Build from source using the instructions below. A signed installer, automatic application updates, and Microsoft Store distribution are not available yet. Background DNS updates are already supported.

![IPKeep Overview with fictional example addresses](docs/images/overview.png)

*Illustrative preview with fictional account, address, and activity data; it is not a live account capture.* [Settings preview](docs/images/settings.png) · [Activity preview](docs/images/activity.png)

## Features

- Token-based setup with a dropdown of your existing hostnames.
- Automatic IP checks, five-minute retries after failures, and pause/resume controls.
- Choice of IPKeep, Amazon Check IP, ipify, or ident.me, with live IPv4 comparisons for VPN users.
- IPv4 and optional IPv6 updates, depending on the selected provider.
- Encrypted token storage using Windows DPAPI and restricted file permissions.
- Searchable activity in a compact table with local timestamps and rotating log files.

## Getting started

1. Build with `./build.ps1`, or extract the complete published `dist/IPKeep` folder to a permanent local folder. Keep the executable, dependencies, Assets, and service folder together.
2. Open `IPKeep.exe`. Viewing status/activity and verifying your own token do not require administrator access.
3. Open **Settings**. For initial service setup, choose **Allow changes** and approve the Windows administrator prompt; the elevated app returns to Settings.
4. Create your hostname and an API token at [admin.ipkeep.net](https://admin.ipkeep.net/). Hostnames can only be created and managed in that admin panel.
5. Paste the token into **1. IPKeep API token** and choose **Load my hostnames**. A verified token is remembered encrypted for this Windows user. Select an existing hostname from **2. Hostname for this computer**. Only this account's enabled hostnames are offered; an empty list directs you to the admin panel.
6. Optionally open **IP lookup service** to compare the IPv4 returned by each provider. IPKeep is selected by default. Choose the service that reports the address you want to publish, especially when a VPN routes some destinations differently.
7. Choose **Save and enable updates**. This verifies the hostname, installs the background service if needed, saves the encrypted token, selected hostname, and lookup provider, and starts the first check. The window can then be closed.

This release supports Windows 10 version 2004 or later and Windows 11, x64. The build bundles .NET and the Windows App SDK, so target computers do not need to install those runtimes separately. It is an unsigned folder distribution, not an MSIX or signed installer. The manager remains in the folder from which you run it; only the service is copied to Program Files.

When the API returns `dnsUpdated: false`, the app reports **IP recorded. DNS publishing pending.** It only reports published DNS when the API confirms it.

## Everyday use

- **Overview:** service state, public addresses detected when the window opens, last/next host update check, and a result for each configured hostname. Anonymous lookup of the provider's supported address families works immediately, even without a token, administrator access, or an installed service; Amazon shows IPv6 as not supported. **Refresh addresses** checks the current connection again. Each address shows its detection time or a specific lookup error. The provider is named, and an unsaved choice is labeled as a preview. Refreshing this preview does not send host updates or change the IPv6 update setting. **Check now** queues a service check without overlapping an active one.
- **Pause updates:** stops the service and changes startup to manual, so updates stay paused after reboot. **Enable updates** restores automatic startup and starts a check.
- **Settings:** restores the remembered token into the masked password field on normal launches. Each opening refreshes the account's active hostname list and preserves the selected hostname if still available. Token editing, verification, and hostname selection work without elevation. Replace the token and choose **Load my hostnames** to verify and remember another account; changing tokens clears the previous list until verified. Verified tokens are remembered even if no hostnames exist yet. Applying service settings still requires **Allow changes**. This setup selects one hostname per computer; it cannot create or type in new hostnames. Existing multi-host configurations continue running until saved with a new selection, and Settings warns that saving replaces them.
- **Restoring an older saved token:** when there is no token remembered for this Windows user, opening Settings requests one Windows administrator approval to recover the existing service token. Approve using the same Windows account; a different administrator account cannot export the token to your profile. Once restored, the app fills the masked field and looks up hostnames automatically, and later launches need no elevation for these lookups. If cancelled, choose **Load my hostnames** to retry or paste the token. The migration helper changes only the user's remembered token and does not change or restart the service. This Windows approval is required by the older credential file's administrator-only permissions.
- **Action messages:** retrying starts with a clear notification area. Successfully loading hostnames clears an earlier restore error; a new validation or storage error still appears if the current attempt fails.
- **Setup visibility:** IP lookup service, Advanced options, and Save and enable updates stay hidden until the token successfully loads at least one active hostname. Changing the token or reloading the list hides them while verification runs; failed/empty lists and rejected tokens keep them hidden. A saved hostname alone does not count as verification. With multiple returned hostnames, choose one to enable Save.
- **IP lookup service:** opening the dropdown checks all providers concurrently over IPv4. Each row changes from Checking to its returned address or Unavailable; hover a failed row for details. Each request has a ten-second timeout, reopening refreshes all results, and old responses cannot overwrite a newer comparison. Lookup requests remain anonymous even though the Settings controls require a verified hostname list. Selecting a provider immediately previews its addresses in Overview; save settings to use it for background updates. Amazon supports IPv4 only, so selecting it turns off IPv6 updates. IPKeep, ipify, and ident.me support both families.
- **Advanced options:** 1–1,440 minutes between checks (default 360), optional IPv6, and ignored IP addresses/CIDR ranges (one per line). IPv4 is always checked. Enable IPv6 only on a connection that supports it. Ignored or unavailable addresses are omitted, preserving the corresponding saved server address.
- **Activity:** the latest 200 entries in a compact **Time (local), Level, Message** table, newest first, refreshed every two seconds. Timestamps are converted to the computer's local timezone using the offset applicable to each event, including daylight-saving changes. The short format is `07 Sep, 14:32:06`; hover a timestamp for its full date/year and offset. Timestamps inside messages, such as the next check time, are also displayed locally. Long messages wrap within their column. Filtering matches both the displayed local text and the original log line. Malformed/incomplete lines remain visible with an unavailable timestamp. **Open log folder** opens the original files, whose full timestamps and offsets are preserved.
- **Service maintenance:** hidden until the token is successfully validated, unless the IPKeep service is already installed. An installed service keeps maintenance visible even when stopped or when the token is missing/rejected, so it can still be repaired or removed. A valid token can reveal maintenance even if its account has no active hostnames; saving updates still requires a hostname. Maintenance installs an updated service from the adjacent `service` folder, or removes the service registration. Changes require administrator access. Removal retains settings and logs. The previous service directory is retained under a uniquely named `service-previous-*` folder when updating, and restored if installation fails.

## Network contract

Customer API tokens authorize exactly two operations: hostname listing and address updates. `GET https://api.ipkeep.net/hosts` returns `{ "hosts": [{ "hostname": "home.a.ipkeep.net" }] }` for the token owner's active hostnames, or an empty array. The client accepts only valid managed hostnames, binds the selection to the token that loaded the list, and retrieves the list again before saving. Empty, failed, or stale lists cannot enable updates.

Updates are `POST https://api.ipkeep.net/update`, with JSON containing `hostname` and the available `ipv4` / `ipv6` fields. Both authenticated endpoints use `Authorization: Bearer <IPKeep token>`. Redirects and cookies are disabled. Their URLs are fixed; there is no editable URL that could receive the token. HTTP response bodies, authentication headers, and tokens are never logged. A 401/403 asks for a new or enabled token; a 404 asks the user to check the hostname/account/paused state.

Client tokens cannot create, delete, rename, enable, or pause hostnames; manage accounts, tokens, authentication or sessions; read account activity; or access internal DNS-node routes. Hostname management requires the admin panel's authenticated browser session. `/ip` is anonymous and is not a third token capability. Keep this two-operation token boundary when proposing changes. The Windows client contains no infrastructure credentials and makes no direct DNS-provider API requests.

Public IP discovery sends anonymous HTTPS GET requests to the selected provider. No lookup provider receives the IPKeep token, hostname, or update body. The free alternatives use these documented endpoints:

| Provider | IPv4 | IPv6 | Response |
| --- | --- | --- | --- |
| IPKeep (default) | `https://api.ipkeep.net/ip` | Same URL over IPv6 | JSON with `ipv4` / `ipv6`; the unobserved family is null |
| [Amazon Check IP](https://docs.aws.amazon.com/us_en/batch/latest/userguide/create-a-base-security-group.html) | `https://checkip.amazonaws.com/` | Not supported | Plain IPv4 address |
| [ipify](https://www.ipify.org/) | `https://api.ipify.org/` | `https://api6.ipify.org/` | Plain IP address |
| [ident.me](https://api.ident.me/) | `https://4.ident.me/` | `https://6.ident.me/` | Plain IP address |

The dropdown follows this order: IPKeep first and selected by default, Amazon Check IP as the preferred alternative, then ipify and ident.me.

The stable `IpLookupProviderId` is saved with the connection and its public settings projection. Existing settings without this field default to `ipkeep`. The service reads it for each check; it never switches automatically after a failure, since another provider may see a different VPN exit. Future providers, including the planned Azure endpoint, belong in `src/IPKeep.Core/IpLookupProviders.cs` with a stable ID and explicit response format. Custom URLs are not supported.

DNS resolution, sockets, and connection pools are kept separate for IPv4/IPv6. Discovery bypasses HTTP proxies, follows the computer's OS/VPN routes, disables redirects/cookies, and retains HTTPS certificate validation. Choosing a provider does not change VPN routing. Null, invalid, private, and wrong-family values are rejected and never clear saved addresses. There are no automatic provider fallbacks or direct Cloudflare DNS API requests; discovery failures use the normal five-minute retry. The default lookup timeout is twenty seconds; dropdown probes use ten seconds.

Every start checks immediately. Successful and ignored checks use the configured interval; failed or partial checks retry after five minutes. Delays begin when a check finishes. A failed hostname does not prevent the other hostnames from updating; a rejected token stops redundant requests for the remaining hosts. A missing enabled address family still allows the other family to update and schedules a retry. Cancellation stops in-flight requests on service shutdown.

## Local storage and service permissions

| Item | Location |
| --- | --- |
| Service executable and dependencies | `%ProgramFiles%/IPKeep/service/` |
| Public settings (no token) | `%ProgramData%/IPKeep/settings.json` |
| Encrypted connection/settings | `%ProgramData%/IPKeep/Private/connection.bin` |
| Remembered desktop token for this Windows user | `%LOCALAPPDATA%/IPKeep/Private/token.bin` |
| Status | `%ProgramData%/IPKeep/Activity/status.json` |
| Log | `%ProgramData%/IPKeep/Activity/service.log` |

The service is named `IPKeep` and runs as **LocalService**. Installation and connection changes require Windows administrator access. Program files and settings are writable only by Administrators and SYSTEM; LocalService can read the connection and write only the Activity directory. Standard users can read public settings and activity, but cannot read the private connection directory. Windows DPAPI encrypts the connection for this computer; protected filesystem permissions restrict who can read its ciphertext. The plaintext token exists only in memory when entered or used. Copying the encrypted connection to another computer is not a supported migration method; enter a token there instead.

The desktop separately remembers verified tokens with DPAPI **CurrentUser**, using a private local directory limited to the current Windows user and SYSTEM. This lets the normal desktop launch retrieve hostname choices without exposing the service's machine-protected connection to standard users. The restored field contains the actual token under password masking. A failed token verification does not overwrite a previously remembered token; errors hide hostname-dependent options. The migration helper checks elevation and the requesting Windows SID, accepts no caller-supplied storage path, and never puts a token on the command line. Saving or clearing service configuration does not implicitly remove this Windows user's remembered token.

The service verifies its installed location and permissions before starting and before every check. It refuses workspace execution. Links/junctions in protected paths are rejected. Settings/status writes use atomic replacement. Logs rotate at approximately 2 MB, retaining the current file plus five archives. IP addresses and hostnames are intentionally present in activity logs; credentials are not.

## Source and build

- `src/IPKeep.Core`: hostname/settings validation, protected storage, HTTP clients, check orchestration, log rotation, service installation/control.
- `src/IPKeep.Service`: Windows service lifecycle, immediate/interval/retry scheduling, cancellation and manual check queue.
- `src/IPKeep.Desktop`: native WinUI 3 application and bundled IPKeep artwork.
- `tests/IPKeep.Tests`: isolated executable test suite with fake HTTP/resolver/update services and temporary files.
- `docs/images`: illustrative app previews containing fictional data.

Build tools: .NET 10 SDK and Windows desktop/WinUI build prerequisites, including the Windows SDK. The project pins Windows App SDK 2.4.0, listed as stable in [Microsoft's downloads](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/downloads). Distribution follows [Microsoft's unpackaged WinUI guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/unpackage-winui-app).

```powershell
git clone https://github.com/calxibe/ipkeep-windows.git
cd ipkeep-windows
./build.ps1                  # tests, then self-contained x64 desktop + service
./build.ps1 -NoTest          # publish without tests
# Tests only:
dotnet run --project tests/IPKeep.Tests/IPKeep.Tests.csproj -c Release
# Optional: also verify the anonymous IPv4 discovery endpoint live (no token):
dotnet run --project tests/IPKeep.Tests/IPKeep.Tests.csproj -c Release -- --live-ipv4
# Optional: verify anonymous IPv4 discovery against every dropdown provider:
dotnet run --project tests/IPKeep.Tests/IPKeep.Tests.csproj -c Release -- --live-providers
```

Building never installs, starts, or stops a service and does not send production updates. Tests cover request destination/authentication, hostname-list parsing, empty/invalid lists, token-bound selection and stale-load rejection, provider persistence/defaults and routing of update addresses, independent concurrent probes and stale-result cancellation, response validation/redaction, error handling, IPKeep JSON and plain-text discovery, separate IPv4/IPv6 transports (including real loopback sockets), hostname/CIDR validation, partial failures, IPv6 omission, ignored networks, cancellation, non-overlapping checks, retry decisions, log retention, atomic saves, Windows encryption, and the workspace runtime guard. Default tests use fake responses and local loopback connections; only the explicit `--live-ipv4` / `--live-providers` options contact public discovery endpoints.

## Validation and remaining release checks

The initial release has 48 isolated tests. These cover HTTP/token boundaries, hostname selection, provider behavior, storage and encryption, scheduling, log handling, and Activity timestamp formatting. Native layouts and live anonymous IPv4 discovery have also been inspected during development. Automated tests do not represent full service-installation or GUI acceptance testing.

Before broad installer distribution, remaining checks include administrator installation, reboot, upgrade/rollback, removal, service-maintenance visibility for existing installations, and live token restoration across launches. Code signing and a full desktop installer remain planned work.

## Contributing and support

Use [GitHub issues](https://github.com/calxibe/ipkeep-windows/issues) for reproducible client bugs and feature requests. Run the isolated tests and build before submitting changes. Do not include real tokens, private settings, or unredacted activity logs in issues or pull requests.

For account help or to report a security issue privately, use [IPKeep support](https://fixquotes.com/docs/contact/?category=ipkeep).

## License

The project is released under the [MIT License](LICENSE). Referenced third-party packages retain their own licenses. This repository does not grant access to the hosted IPKeep service; users supply their own account token.
