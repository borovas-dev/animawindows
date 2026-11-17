# animawindows

A Windows-native port of [animadroid](https://github.com/borovas-dev/animadroid): an anime streaming client built with
WinUI 3 / Windows App SDK (C#, .NET 11), packaged as MSIX.

## Layout

| Path | Purpose |
| --- | --- |
| `AnimaWindows.Core` | Platform-free models, contracts (interfaces) and pure logic |
| `AnimaWindows.Providers` | Anime source providers (AngleSharp scraping) |
| `AnimaWindows.Data` | SQLite repositories and settings store |
| `AnimaWindows.App` | WinUI 3 app (MSIX, x64 + ARM64) |
| `tests/` | xunit v3 test projects |
| `config/providers.json` | Remote provider configuration (bundled copy) |
| `docs/ARCHITECTURE.md` | Project map, contracts, conventions |
| `src/` | Read-only reference checkouts (the Kotlin Android app). Never edit. |

## Build

The libraries and tests build on any OS with the .NET 11 SDK:

```
dotnet test tests/AnimaWindows.Core.Tests --filter "Category!=Live"
```

The app needs Windows 10 19041+ and the .NET 11 SDK with the Windows App SDK workload prerequisites:

```
dotnet build AnimaWindows.App/AnimaWindows.App.csproj -c Release -p:Platform=x64
# unsigned MSIX
dotnet build AnimaWindows.App/AnimaWindows.App.csproj -c Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=false
```

Use `-p:Platform=ARM64` for ARM64. Do not build the whole `.slnx` on non-Windows machines (the app project is Windows-only).

## License

GPL-3.0, like the original project.
