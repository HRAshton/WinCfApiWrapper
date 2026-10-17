# WinCfApiWrapper

[![CI](https://github.com/HRAshton/WinCfApiWrapper/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/HRAshton/WinCfApiWrapper/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/WinCfApiWrapper)](https://www.nuget.org/packages/WinCfApiWrapper)
[![0BSD License](https://img.shields.io/badge/license-0BSD-blue)](LICENSE)
![.NET 8+](https://img.shields.io/badge/.NET-8%2B-512BD4)
![Windows x64](https://img.shields.io/badge/platform-Windows%20x64-0078D6)

A .NET library for exposing caller-provided content containers as Windows Cloud Files sync roots. The built-in `ZipContainer` supports ZIP sources; callers can implement `IContentContainer` for other formats and storage systems.

See [the library README](src/WinCfApiWrapper/README.md) for usage, the container contract, and mount lifecycle details.

Releases are versioned with GitVersion and published to NuGet on Thursdays only when `master` has new commits. See [release automation and provenance](docs/RELEASING.md) for the setup and verification steps.
