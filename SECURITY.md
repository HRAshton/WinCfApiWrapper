# Security Policy

## Supported versions

Security fixes are provided for the latest published version of **WinCfApiWrapper**.
Older releases may not receive backported fixes. See
[NuGet](https://www.nuget.org/packages/WinCfApiWrapper) for the newest version.

| Version | Security updates |
| --- | --- |
| Latest published release | Supported |
| Earlier releases | Not guaranteed |

## Reporting a vulnerability

**Do not disclose vulnerabilities, exploit details, or proof-of-concept code in public
GitHub issues, pull requests, or discussions.**

Please use GitHub's private vulnerability reporting:

1. Open the repository's [Security advisories](https://github.com/HRAshton/WinCfApiWrapper/security/advisories) page.
2. Select **Report a vulnerability** (if available).
3. Provide the affected version, impact, reproduction steps, and a suggested mitigation, if known.

If private reporting is unavailable, use any private contact method listed on
[the maintainer's GitHub profile](https://github.com/HRAshton). If none is
available, open a **public issue requesting a private reporting channel only**,
without including the vulnerability details, and coordinate a secure channel
with the maintainer before sending sensitive information.

## Coordinated disclosure

Please allow the maintainer time to confirm, investigate, and remediate an issue before
public disclosure. We will assess the report, coordinate an appropriate remediation and
disclosure plan with the reporter, and use a GitHub Security Advisory when applicable.

Security updates will be released through the regular versioned NuGet and GitHub release
channels. Critical fixes may be released outside the usual schedule.

## Scope

Reports about ZIP archives and path handling, native Cloud Files API interactions,
privilege boundaries, data integrity, or dependencies are especially useful. Please
include the affected Windows/.NET versions and whether the behavior requires an
attacker-controlled archive or filesystem path.
