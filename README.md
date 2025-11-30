# WinCfApiWrapper

A .NET library for exposing caller-provided content containers as Windows Cloud Files sync roots. The built-in `ZipContainer` supports ZIP sources; callers can implement `IContentContainer` for other formats and storage systems.

See [the library README](src/WinCfApiWrapper/README.md) for usage, the container contract, and mount lifecycle details.