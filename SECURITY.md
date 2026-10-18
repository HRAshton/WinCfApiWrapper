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

## Response and triage expectations

The maintainer aims to acknowledge a private vulnerability report within **7 calendar
days** and provide a progress update at least every **14 calendar days** while it is
being investigated. These are best-effort targets, not guaranteed remediation
deadlines; response may be slower when the sole maintainer is unavailable.

Reports are triaged based on reproducibility, exploitability, impact, and affected
supported versions. If the report is out of scope or not reproducible, the
maintainer will explain the assessment through the private reporting channel.
Where a fix is needed, the reporter and maintainer should agree on a reasonable
disclosure timeline without exposing users to avoidable harm.

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
