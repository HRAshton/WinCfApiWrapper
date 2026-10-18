# Contributing to WinCfApiWrapper

Thanks for helping improve WinCfApiWrapper. Bug reports, documentation updates, tests, and focused pull requests are welcome.

## Before starting

- Search [open issues](https://github.com/HRAshton/WinCfApiWrapper/issues) and existing pull requests to avoid duplicate work.
- For substantial API changes or new container formats, open an issue describing the use case and proposed design first.
- **Security vulnerabilities must not be disclosed in public issues or PRs.** Follow [SECURITY.md](SECURITY.md).
- Contributions are licensed under the project's [0BSD license](LICENSE). Files must satisfy the repository's [REUSE](https://reuse.software/) policy.

## Local environment

Use Windows x64 with Cloud Files API support (Windows 10 1709+), the .NET 10 SDK, and the .NET 8 and 10 runtimes. The main library targets `net8.0-windows`. Offline native integration tests need permission to register Windows sync roots.

Start with [the development guide](docs/development.md) for exact build/test commands and environment details. For application code, read the [project README](README.md) and [container contract](docs/containers.md).

## Contribution workflow

1. Fork the repository, create a topic branch from `master`, and keep changes narrowly scoped.
2. Preserve public API compatibility when possible; explain breaking changes before implementing them.
3. Include regression tests for bug fixes and tests for new behavior. Add Windows integration coverage when native behavior changes.
4. Follow `.editorconfig`, nullable reference type rules, and existing StyleCop conventions.
5. Update the relevant README or files in `docs/` when changing behavior or contracts.
6. Run the applicable commands from [docs/development.md](docs/development.md) and describe any tests you could not run.
7. Open a pull request with the motivation, implementation summary, testing evidence, and compatibility/security implications.

The repository uses required CI checks and protected `master` rules. Do not try to bypass protection or suppress analysis to make a PR pass. Maintainers review changes under the configured GitHub rulesets.

## Change-specific guidance

- **Container or archive changes:** Exercise traversal, reserved Windows names, duplicate paths, file/directory conflicts, cancellation, and malicious/oversized input. See the [security model](docs/security-model.md).
- **Hydration and native interop:** Cover callback cancellation, concurrent fetches, partial transfer failures, mount rollback, and safe cleanup. Prefer offline deterministic tests plus Windows integration tests where possible.
- **Dependencies and workflows:** Explain why a dependency or permission is needed. Keep third-party GitHub Actions pinned to full commit SHAs; avoid broadening workflow token permissions.
- **Releases:** Do not manually replace published packages or retarget release tags. See [docs/releasing.md](docs/releasing.md).

## Reporting problems

For regular bugs, include the library version, Windows build, .NET version, minimal reproduction, expected versus actual behavior, and relevant logs without credentials or private file content. Use private vulnerability reporting for security issues.

The maintainer may request changes or defer features that broaden platform support, increase attack surface, or cannot be tested reliably.
