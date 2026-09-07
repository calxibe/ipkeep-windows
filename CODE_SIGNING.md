# Code signing policy

## Current status

IPKeep is preparing an application to [SignPath Foundation](https://signpath.org/) for free signing of its MIT-licensed Windows client. Approval and signing are not yet in place. All current preview downloads are **unsigned**. This policy does not claim Foundation membership, endorsement, or an issued certificate.

If approved and once signed releases are available, those releases will carry this attribution: Free code signing provided by [SignPath.io](https://about.signpath.io/), certificate by [SignPath Foundation](https://signpath.org/).

## Maintainer and signing roles

| Role | Responsible maintainer |
| --- | --- |
| Author / committer | [@calxibe](https://github.com/calxibe) |
| Reviewer of external contributions | [@calxibe](https://github.com/calxibe) |
| Proposed release-signing approver | [@calxibe](https://github.com/calxibe) |

Signing accounts must use multi-factor authentication. Every signing request must receive the maintainer's manual approval. Signing credentials must never be committed to source or exposed to pull-request builds. These are requirements for future signing, not a claim that SignPath accounts already exist.

## Build and artifact scope

The [GitHub workflow](https://github.com/calxibe/ipkeep-windows/blob/main/.github/workflows/build.yml) tests and builds the public source on GitHub-hosted Windows runners, then retains the unsigned binaries as a workflow artifact. Preview release archives include the source commit and SHA-256 checksum. No SignPath submission step is enabled before onboarding.

Only project-owned executables and assemblies are intended for signing: `IPKeep.exe`, `IPKeep.dll`, `IPKeep.Core.dll`, `service/IPKeep.Service.exe`, `service/IPKeep.Service.dll`, and `service/IPKeep.Core.dll`. Bundled Microsoft runtime and other third-party files retain their upstream signatures and licenses; they must not be re-signed as IPKeep. An installer is not yet included.

See the [Windows client privacy policy](PRIVACY.md) for network requests and local data, and the [IPKeep service privacy policy](https://ipkeep.net/privacy) for hosted account data.
