# Security model and limitations

This guide describes the library's trust boundaries, safeguards, and remaining risks. Report vulnerabilities privately through [SECURITY.md](../SECURITY.md).

## Intended use and trust boundaries

WinCfApiWrapper mounts caller-provided read-only container content as Windows Cloud Files placeholders on an NTFS sync root. The host application controls the container provider, source bytes, mount base directory, and permissions. The library is not a sandbox, malware scanner, or bidirectional filesystem synchronization engine.

Important boundaries are: **untrusted archive/remote content -> container parser -> normalized entries -> filesystem placeholders -> Cloud Files native callbacks**. Compromise or malformed behavior at any boundary may have consequences for the host application's filesystem, memory, and availability.

## Existing safeguards

- Container paths are normalized; traversal, absolute paths, reserved device names, invalid Windows characters, duplicate paths, and file/directory conflicts are rejected.
- Native fetch ranges are validated; bounds, concurrency, cancellation, and error handling have unit/property and integration coverage.
- Existing placeholders are checked before resuming a mount; mismatches cause an error rather than silently reusing stale state.
- Cleanup recognizes fingerprinted mount names, and mount operations reject volume-root base paths. Unregistered reparse-point mount roots are rejected during cleanup.
- Releases are built through protected automation with verifiable provenance and package verification.

## Residual risks and operator responsibilities

### Resource exhaustion and malicious archives

ZIP/TAR inputs can contain huge numbers of entries, extreme decompression ratios, oversized files, invalid metadata, and expensive read patterns. TAR and single-file compressed containers buffer decoded data in memory. Built-in buffered containers now apply configurable decoded-byte and entry-count budgets via `ArchiveResourceLimits` (defaults: 256 MiB and 250,000 entries). ZIP indexing applies an entry-count budget and offers an **opt-in** cumulative raw central-directory record-byte budget (`ArchiveResourceLimits.MaxCentralDirectoryMetadataBytes`; `null` by default). The latter rejects before allocating the next over-budget raw record but does not limit `ZipArchive`'s internal directory parsing. Streaming ZIP hydration does not enforce a total decompressed-byte cap. These are not exact process-memory or CPU budgets: allocations can exceed decoded output, and a malicious stream may consume work before failing. For untrusted inputs, also prevalidate sources and consider a separate worker with OS-enforced resource budgets.

### Identity and source immutability

Automatic mount paths now use a versioned `.rzm-v2-` folder suffix containing **128 bits** of the already-computed listing SHA-256 instead of the previous 32-bit prefix, protecting against the demonstrated short-prefix collision. Existing eight-hex `TrustedContentId` mounts retain their original `.rzm-xxxxxxxx` names and caller-uniqueness contract. Legacy automatic directories are never silently deleted or reused by v2 mounting; both formats remain recognizable for **explicit** cleanup under a dedicated owned parent.

A container fingerprint is a digest of the *declared listing and version metadata*, not proof that remote bytes have remained unchanged. `ZipContainer` derives per-entry versions from compressed length and CRC32, which are **not cryptographically collision-resistant**. Provide immutable archives and stable remote object versions; do not rely on CRC32 as an authenticity or tamper-resistance mechanism. Validate downloads independently when integrity matters.

Trusted-source options deliberately relax consistency checks: `TrustedContentId` bypasses listing hashing, `SkipResumeValidation` skips placeholder metadata checks, and ZIP `validateEntryMetadata: false` skips rereading the selected central record. These options require an immutable source and an unchanged app-owned mount folder. Reusing a trusted ID for different content can expose stale hydrated files. Default settings retain consistency checks; safe-path and snapshot-membership validation remain enforced with trusted settings. The built-in HTTP ZIP source enforces exact ranges and remote version validators and rejects full-response fallbacks.

### Trusted build-artifact performance contract

For controlled, immutable production build artifacts, the host enables `TrustedContentId` and `SkipResumeValidation = true` and constructs ZIP containers with `validateEntryMetadata: false`. The host assumes responsibility for snapshot-ID uniqueness, remote byte immutability and exclusive ownership of the mount directory. This intentionally skips listing hashing, per-placeholder resume validation and selected ZIP record revalidation, but not initial indexing, path safety or CFAPI requirements. Do not silently add mandatory CRC/SHA, additional source I/O, per-placeholder checks or arbitrary untrusted-input caps to this trusted fast path. See [the Windows benchmark and no-regression contract](trusted-benchmark.md). These guarantees do **not** apply to arbitrary untrusted archives.

### Paths, permissions, and cleanup

Use a **dedicated application-owned NTFS parent**, not a volume root, shared folder, or user-data directory. Its ACL must prevent untrusted users/processes from creating, renaming, replacing, or reparenting any mount directory or child. The parent and all ancestor directories must be protected from attacker-controlled substitutions. Call cleanup only **after a successful native disconnect**: filename conventions alone do not convey ownership.

During cleanup only, registered mount roots must match the WinCfApiWrapper Cloud Files provider name, and cleanup rejects root or nested **directory** reparse points (Cloud Files file placeholders are allowed). A previously unregistered folder is deleted only if an earlier cleanup/rollback attempt wrote a **sibling** `<mount-root>.wincfapiwrapper-pending-cleanup` marker in the dedicated private parent; unrelated `.rzm-*` folders without that marker are refused. The sibling file survives **partial recursive deletion** of mount contents and is removed after successful cleanup. The marker is **not a cryptographic ownership proof**: a user able to write the app's private parent can forge it. Rollback and unregister write it as necessary so failed deletion can be retried. Both legacy and v2 recognized mount names retain this policy.

**Residual limitation:** provider display names are not cryptographic identities, and checks made by pathname cannot close a concurrent rename/reparse path-swap race against an adversary with write permission. .NET recursive deletion does not intentionally traverse directory reparse points, but hostile write access can still invalidate pathname-based safety. Do not grant untrusted users access to the parent; strong handle-relative, file-ID-verified recursive deletion would be needed to remove this remaining race. Cleanup errors fail closed and may require manual inspection.

### Native integration and failure modes

CFAPI operations may fail because of OS settings, filesystem attributes, races, inaccessible files, remote I/O, cancellation, or insufficient rights. Errors should be surfaced to the user; log non-sensitive diagnostics without exposing secrets or private file content. Code coverage reports intentionally exclude some Windows API adapter files, so unit-test coverage percentages are not a complete measure of native security assurance.

### Dependencies and build supply chain

Dependencies are updated by Renovate, and release GitHub Actions are pinned to exact commits. Major dependency upgrades still deserve scrutiny for compatibility and supply-chain risk. Verify published release attestations using [releasing.md](releasing.md); provenance identifies the build origin but does not establish that upstream input or produced software is free of vulnerabilities.

## Security review checklist

For changes involving archive parsing, mount paths, hydration, deletion, or native APIs:

1. Identify the trusted and untrusted inputs and the host process privileges.
2. Validate bounds, integer arithmetic, paths, versions, and file/directory collisions.
3. Define resource budgets for maliciously large inputs and bounded concurrent work.
4. Cover cancellation, partial failure, rollback, retries, and race conditions.
5. Add deterministic regression tests and applicable Windows native integration tests.
6. Document any remaining limitation rather than implying all inputs are safe.
