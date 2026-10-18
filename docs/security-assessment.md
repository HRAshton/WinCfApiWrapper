# Security assessment

**Assessment date:** 2026-10-18  
**Scope:** WinCfApiWrapper source and checked-in tests, GitHub Actions, dependencies,
and published NuGet release process as documented in this repository.  
**Method:** Source-informed threat assessment and test inventory. This is not an
independent security audit, penetration test, or claim of complete verification.
The assessment does not establish that all security-related tests have passed
on all possible Windows versions.

See [security-model.md](security-model.md) for the consumer-facing operational
assumptions, and [SECURITY.md](../SECURITY.md) for private reporting.

## Purpose and assets

The library mounts caller-provided, read-only container data as Windows Cloud Files
placeholders. Security goals are to preserve filesystem boundaries, prevent
out-of-range native transfers, limit failure propagation and resource exhaustion,
avoid deleting non-owned files, and ensure published packages match their
attested build inputs.

Assets at risk include host-process availability, mounted file contents, host
filesystem data, and the integrity of NuGet releases. The host application
is trusted to choose appropriate filesystem permissions and a dedicated
app-owned mount parent; archive bytes, remote sources, and container paths
may be attacker-controlled.

## Trust boundaries

1. **Archive or remote source -> parser/container**: metadata, paths, sizes,
   compressed data, and streams may be malformed or change unexpectedly.
2. **Container listing -> filesystem**: normalized relative paths determine
   placeholder names; Windows path rules and conflicts must be enforced.
3. **Windows callbacks -> managed hydration -> native transfer**: concurrent
   and canceled requests must not transfer out-of-bounds data.
4. **Application-owned mount directory -> cleanup**: the deletion API must only
   be used on disposable application-owned paths.
5. **Source control -> CI -> NuGet**: third-party dependencies and workflow
   permissions can affect release integrity.

## Risks, current controls, and residual gaps

| Area | Existing safeguards and evidence | Residual risk / action |
| --- | --- | --- |
| Malicious archive paths | `ContainerPath.Normalize` rejects absolute and traversing paths, Windows reserved names, and invalid segments; `ContainerCatalog` and `ZipCatalog` reject duplicates and file/directory conflicts. Unit/property tests cover path boundaries and container catalogs. | Continue hostile ZIP/TAR corpus tests, including Windows normalization edge cases, nested paths, and conflicting archive metadata. |
| Decompression and resource exhaustion | Fetch queue/workers and hydration concurrency are bounded. `ArchiveResourceLimits` caps buffered decoded bytes (default 256 MiB) and entry counts (default 250,000); ZIP enforces entry counts while streaming contents. `ArchiveResourceLimitsTests` exercises TAR, TAR.GZ, ZIP, GZip, Brotli, Deflate, and ZLib boundaries. | **Residual availability risk:** limits do not cap temporary memory allocation, decompression CPU, or ZIP hydration output size. Use an isolated worker with OS-enforced limits for hostile inputs and consider additional decompression-work controls. |
| ZIP content changes and identity | Container fingerprints are computed from the listing and supplied version values. `ZipContainer` uses CRC32 and compressed length as entry version metadata. | **Open when content is mutable/adversarial.** CRC32 is not an authenticity hash. Pin immutable remote objects / archive versions, fail on unexpected changes, and consider stronger identities where integrity must be guaranteed. |
| Mount cleanup and filesystem access | Volume-root mount base paths are rejected; recognized fingerprinted roots are required; unregistered reparse-point roots are rejected. Tests exercise cleanup and rollback. | **High impact if the caller supplies a shared/writable directory.** Do not consider filename markers proof of ownership. Document and test permissions/reparse-point races; run cleanup only under a dedicated access-controlled parent. |
| CFAPI callbacks and hydration | `TransferRange` validates bounds, fetch coordination is bounded, cancellations/errors have unit/integration tests, and native transfer failures propagate. | Windows native adapters are excluded from unit coverage reporting. Expand native integration/fault-injection coverage for cancellation, incomplete transfers, and teardown under load. |
| TAR special entries | `TarArchiveDecoder` accepts regular files and directories; it rejects other entry types such as links rather than materializing them as filesystem links. | Keep explicit regression tests for TAR symlinks, hard links, and malformed headers. |
| Dependencies and releases | SHA-pinned Actions, Renovate, CodeQL, NuGetAudit of direct/transitive dependencies with `NU1901`–`NU1904` errors, NuGet Trusted Publishing, attested packages, and package-payload verification. | Review major upgrades manually; investigate third-party dependency advisories and workflow-permission changes. Provenance verifies origin, not freedom from vulnerabilities. |

## Evidence and limitations

Relevant checked-in tests and tools:

- `tests/WinCfApiWrapper.Tests/Fuzzing/InputBoundaryProperties.cs` — FsCheck
  properties for path handling and transfer ranges. These are property tests,
  **not** coverage-guided archive parser fuzzing.
- `tests/WinCfApiWrapper.Tests/Archives/` and
  `tests/WinCfApiWrapper.Tests/Mounting/` — parser/catalog and mounting unit tests.
- `tests/WinCfApiWrapper.Tests/Hydration/` — fetch, cancellation, and transfer tests.
- `tests/WinCfApiWrapper.IntegrationTests/` — Windows-specific offline,
  stress, rollback, cleanup, and opt-in live tests.
- `.github/workflows/ci.yml`, `tests/coverage.runsettings`, and
  `.github/workflows/scorecard.yml` — automated testing, scoped coverage,
  dependency checks, and supply-chain assessment. GitHub-managed CodeQL
  runs separately from the checked-in workflows.
- `.github/workflows/release.yml` and
  `.github/workflows/attested-release-build.yml` — separate build/publish
  stages, attestation, and release verification.

This review identifies controls from source and committed tests; it did **not**
execute hostile-archive experiments, red-team a Windows host, or independently
verify private GitHub security settings. Existing 95% coverage enforcement
excludes selected native adapter files and is not a security proof.

## Follow-up actions

1. **High:** Decide whether accepting untrusted compressed content is a
   supported threat model. Maintain and test the decoded-byte/entry-count
   budgets, and consider CPU/temporary-memory limits and ZIP hydration output
   controls for hostile inputs.
2. **High:** Make immutable-source guarantees explicit for remote ZIP inputs;
   explore stronger version identities and changed-source failure tests.
3. **Medium:** Add Windows-specific reparse point, access-control, and cleanup
   race tests with a disposable, dedicated test directory.
4. **Medium:** Add native failure-injection and transfer-cancellation coverage;
   document CI coverage exclusions in review discussions.
5. **Ongoing:** Review major dependency upgrades and release/workflow changes;
   reassess after security-impacting changes.

These items remain **proposed**, not accepted-risk decisions or promises
of a fix. Record decisions and test evidence in future issues/PRs, then
update this document as mitigations are implemented.
