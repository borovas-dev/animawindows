# Testing

## Automated (CI)

`.github/workflows/ci.yml` runs on every push:

- **test** (ubuntu-latest): `AnimaWindows.Core.Tests`, `AnimaWindows.Providers.Tests`, `AnimaWindows.Data.Tests`, excluding `[Trait("Category","Live")]`.
- **app** (windows-latest, x64 + ARM64): fetches libmpv, builds `AnimaWindows.App` with warnings as errors, produces an unsigned MSIX artifact.

Live provider smoke tests hit the real sites and are opt-in:

```bash
dotnet test --project tests/AnimaWindows.Providers.Tests -- --filter-trait "Category=Live"
```

## Installing a CI build

CI signs every `.msixbundle` with the project certificate (see `signing/README.md`) and fails if the signature is missing. Trust the certificate once per machine, in **Local Machine › Trusted People** (the Current User store is not used for package installs), from an elevated PowerShell:

```powershell
Import-Certificate -FilePath .\animawindows-signing.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

Then double-click the `.msixbundle` from the `animawindows-msix-<platform>` artifact.

## Manual checklist (not covered by CI)

1. **Launch**: window opens with Mica, title bar search, Home/Search/Downloads/Settings; second launch focuses the existing window.
2. **Search**: query in English (HiAnime/KickAssAnime/AnimeParadise) and set Content language to Polish (Shinden). SUB/DUB toggle filters results.
3. **Cloudflare**: if a provider is challenged, the "Verify in browser" dialog appears, clears, and the search retries.
4. **Details**: cover, synopsis, episode list; Play / Continue / Replay label follows watch history; favourite toggle shows up on Home.
5. **Playback (native engine)**: video starts, seek/volume/speed/quality/subtitle controls work, controls auto-hide, keyboard (Space, J/L, arrows, M, F/F11, Esc, N/P, S, C).
6. **Skip**: on an episode with AniSkip data the skip button appears (or auto-skips with Undo per settings).
7. **mpv fallback**: set Engine = mpv in Settings; playback works with mpv's on-screen controls; Auto falls back to mpv when native fails (e.g. a stream with separate audio).
8. **Resume**: leave mid-episode, reopen — resumes at the saved position; Home shows Continue watching.
9. **Media keys / SMTC**: the Windows media flyout shows title and cover; hardware play/pause/next work.
10. **Picture-in-picture**: compact overlay button keeps video on top; fullscreen toggles.
11. **Downloads**: download an episode and a range from Details; progress toast and taskbar progress; pause/resume/delete; play offline with networking disabled.
12. **Tracking** (builds with `-p:AniListClientId=… -p:MalClientId=…`): connect AniList and MAL via the browser, return through `animawindows://auth/...`, finish an episode past 85 % and check the list updates.
13. **Settings**: theme switch, backup export/import, ani-cli `ani-hsts` import, Source health page runs all providers.
14. **Localisation**: switch Windows display language to Polish and Chinese (Simplified); all UI strings translated.
15. **Accessibility**: navigate everything with keyboard only; Narrator reads button names; High contrast theme renders correctly.
