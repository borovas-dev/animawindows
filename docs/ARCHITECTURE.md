# Architecture

Windows (WinUI 3) port of the Kotlin app in `src/animadroid` (read-only reference). Every contract in Core cites the Kotlin
file it mirrors; when in doubt, the Kotlin behaviour wins.

## Project map

| Project | TFM | Depends on | Contents |
| --- | --- | --- | --- |
| `AnimaWindows.Core` | net11.0 | - | models, `Abstractions/` (all contracts), `Json/`, pure services in reserved folders |
| `AnimaWindows.Providers` | net11.0 | Core, AngleSharp | one folder per provider |
| `AnimaWindows.Data` | net11.0 | Core, Microsoft.Data.Sqlite | SQLite repositories, settings store, backup |
| `AnimaWindows.App` | net11.0-windows10.0.26100.0 | all | WinUI 3 UI, platform implementations (WebView2 solver, network cost, secret store, playback engines), DI composition root |
| `tests/*.Tests` | net11.0 | the project under test | xunit v3 |

Dependency rule: Core knows nothing about other projects or Windows APIs. Anything needing Windows (WebView2, WinRT, mpv,
DPAPI) is behind a Core interface and implemented in `AnimaWindows.App`.

## Namespaces

- `AnimaWindows.Core.Models` - all records/enums/exceptions (`Models/`).
- `AnimaWindows.Core.Abstractions` - all interfaces (`Abstractions/`).
- `AnimaWindows.Core.Json` - `CoreJsonContext`.
- `AnimaWindows.Core.Net` - `NetworkConstants`, later HTTP client factory.
- `AnimaWindows.Core.{Chain,AniSkip,Subtitles,Playback,RemoteConfig,Health,Updates,Backup,Tracking,Downloads}` - services.
- `AnimaWindows.Providers.{HiAnime,KickAssAnime,AnimeParadise,Shinden,Hosts,AniLibria,AnimeFire,Common}`.
- `AnimaWindows.Data`, `AnimaWindows.App`.

Namespace/type clash rule: no type may be named like a sibling namespace segment (e.g. the remote-config document is
`RemoteConfigDocument`, because `AnimaWindows.Core.RemoteConfig` is a namespace).

## Contracts (all in `AnimaWindows.Core/Abstractions`)

| Contract | Mirrors (Kotlin) | One line |
| --- | --- | --- |
| `IAnimeProvider` | provider/api `AnimeProvider` | search / details / episodes / streams for one site; metadata: Id, DisplayName, ContentLanguages, ReportsDubAvailability, GfwBlocked, Priority |
| `IProviderRegistry` | core/model `ProviderRegistry` | ids of registered providers |
| `IProviderChain` | provider/api `ProviderChain` | language-aware ordering, parallel merged search, failover stream resolution |
| `IProviderAvailability` | `ProviderAvailability` | remote kill switch consulted by the chain |
| `IProviderConfig` / `IRemoteConfigSource` | core/remoteconfig | per-provider base URLs, mirrors, params; document owner with `RefreshAsync` |
| `ILibraryRepository` | core/model `LibraryRepository`, Room DAOs | search history, progress, continue-watching, favourites |
| `IDownloadRepository` | core/model `DownloadRepository` | persistence of download rows |
| `IDownloadController` | core/model `DownloadController` | enqueue / pause / resume / remove downloads |
| `ISettingsStore` | `SettingsRepository`, datastore | `Current`, `UpdateAsync(transform)`, `Changed`, JSON export/import |
| `ISyncRepository` | core/sync `SyncRepository` | AniList/MAL OAuth + queued progress updates |
| `ISkipTimesRepository`, `IMalIdResolver` | feature/aniskip | AniSkip opening/ending lookup; MAL id resolution |
| `IBackupService` | core/database `BackupManager` | JSON backup/restore, Android-compatible |
| `ICloudflareSolver` | core/network `CloudflareSolver`, `CloudflareSolved` | WebView2 challenge solving, cookie sharing, `Solved` event |
| `ISecretStore` | core/sync `TokenStore` | encrypted string key/value for tokens |
| `INetworkCostProvider` | downloads `DownloadPolicy` requirements | Offline / Unmetered / Metered + change event |
| `IClock`, `IAppPaths` | - | time source; data / cache / downloads directories |
| `IPlaybackEngine` | feature/player (ExoPlayer) | load/play/pause/seek/rate/volume/mute, state + position/ended/failed events, opaque `VideoSurface`; never renders subtitles |

Observation convention (decided once): repository change streams are `IAsyncEnumerable<T>` that yield the current value first,
then every distinct change, until cancelled (documented on `ILibraryRepository`). Settings, sync state, network cost, remote
config and playback use plain .NET `event`s instead because they are in-memory singletons with a synchronous `Current`.

## Conventions

- C# `latest`, nullable on, file-scoped namespaces, warnings are errors. Records for models; positional for simple ones,
  `init` properties (with `required`) when defaults are non-constant (collections).
- Naming: `PascalCase` members, `_camelCase` private fields, async methods end in `Async`, change-stream methods start with `Observe`.
  Kotlin `providerId`/`animeId`/`episodeNumber` keep their meaning; episode numbers are strings ("12.5").
- Timestamps: Unix epoch milliseconds (`long`), from `IClock`. Positions: `long` ms in persisted rows, `TimeSpan` in the engine.
- JSON: System.Text.Json source generation only, via `CoreJsonContext` (camelCase, enums as Kotlin constant names such as `SUB`,
  `BEST_QUALITY` via `[JsonStringEnumMemberName]`, defaults written, unknown fields ignored). Computed properties carry
  `[JsonIgnore]`. Provider-private DTOs get their own `JsonSerializerContext` in the provider folder. Never call reflection-based overloads.
- Errors: expected provider failures throw `ProviderException` (or subtypes `NoProviderAvailableException`,
  `ProviderTimeoutException`); Cloudflare throws `CloudflareChallengeException` (deliberately not a `ProviderException`);
  best-effort services (AniSkip, remote config refresh, downloads enqueue) never throw except for cancellation.
- Cancellation: every async API takes a trailing `CancellationToken cancellationToken = default`, passes it down, never swallows
  `OperationCanceledException`.
- Providers read their base URL/params from `IProviderConfig.ForProvider(id)` on each call (mirrors can change at runtime).
- No UI types in Core/Providers/Data. No static mutable state.
- Tests: one test project per library, `Fixtures/` copied to output (HTML/JSON captured from sites). Tests that hit the real
  network must be marked `[Trait("Category", "Live")]`; CI runs `--filter "Category!=Live"`.

## Reserved folders (work packages)

- Providers: `AnimaWindows.Providers/{HiAnime,KickAssAnime,AnimeParadise,Shinden,Hosts,AniLibria,AnimeFire,Common}`
- Core services: `AnimaWindows.Core/{Net,Chain,AniSkip,Subtitles,Playback,RemoteConfig,Health,Updates,Backup,Tracking,Downloads}`
  (`Chain` = `IProviderChain` implementation, `AniSkip` = `SkipController` + repository, `Updates` = GitHub release checker producing `UpdateState`, `Backup`/`Tracking`/`Downloads` implement the matching contracts.)
- Data: everything in `AnimaWindows.Data/` (SQLite schema mirroring the Room entities, `ILibraryRepository`, `IDownloadRepository`, `ISettingsStore`, `IBackupService`).
- App: `AnimaWindows.App/` (UI, DI root, `ICloudflareSolver`, `INetworkCostProvider`, `ISecretStore`, `IAppPaths`, `IPlaybackEngine` implementations).
