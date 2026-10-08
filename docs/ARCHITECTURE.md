# Architecture

Hush is a WinUI 3 app on .NET 10, split into five projects so that everything depending on YouTube's private API, and
everything Windows-specific, stays in its own place.

```
src/
  HushMusic.Core/       models, interfaces, queue, playback actions, radio, Last.fm, features
  HushMusic.InnerTube/  YouTube Music's InnerTube API: HTTP client (Http/), API classes (Api/), parsers (Parsing/)
  HushMusic.Auth/       cookie sign-in, DPAPI session and secret storage, SAPISIDHASH signing, yt-dlp cookie file
  HushMusic.Playback/   yt-dlp and Deno management, stream resolution, MediaPlayer, SMTC (media keys and flyout)
  HushMusic.App/        WinUI 3 shell, pages, view models, mini player, tray icon, taskbar player, updates
tests/
  HushMusic.InnerTube.Tests/  parser tests on saved real responses (Fixtures/) and HTTP request tests
  HushMusic.Core.Tests/       queue, playback actions, features, radio, Last.fm
  HushMusic.Playback.Tests/   yt-dlp output parsing, Deno installer, JavaScript runtime choice
```

| Project | Target | Depends on |
|---|---|---|
| Core | `net10.0` (no Windows UI) | Microsoft.Extensions abstractions only |
| InnerTube | `net10.0` | Core |
| Auth | `net10.0-windows` | Core (DPAPI through `Windows.Security.Cryptography`) |
| Playback | `net10.0-windows` | Core (`Windows.Media.Playback`) |
| App | `net10.0-windows`, WinUI 3 | all of the above |

Everything is wired with `Microsoft.Extensions.Hosting` and dependency injection. Each project has a
`ServiceCollectionExtensions` that registers its services; the App composes them in `Hosting/AppHost.cs`.

## Isolating the unofficial parts

There is no official YouTube Music API. Three boundaries keep the unofficial pieces contained, so that when YouTube
changes something the fix lives in one project:

| Concern | Interfaces (in Core) | Only implementation |
|---|---|---|
| YouTube Music's private API | `IBrowseApi`, `ISearchApi`, `ILibraryApi`, `IWatchApi`, `IAccountApi` | `HushMusic.InnerTube` |
| Finding a playable stream | `IStreamResolver` | `HushMusic.Playback` (yt-dlp); live radio uses its own resolver in Core, never yt-dlp |
| Signing in and signing requests | `IAuthService`, `IRequestAuthenticator` | `HushMusic.Auth` |

Rules that follow from this:

- Every InnerTube request and parser follows [ytmusicapi](https://github.com/sigma67/ytmusicapi). Endpoints, bodies,
  headers and response paths are not invented; where Hush deviates, the docs say so and why
  ([innertube-requests.md](innertube-requests.md), [parsers-spec.md](parsers-spec.md)).
- Responses are UI render trees. There is one parser per page type, and it maps the tree to clean models (`Track`,
  `Album`, `Artist`, `Playlist`, `Shelf`, ...).
- Parsers are null-safe: a missing field logs a warning and skips that item; it never fails the page.
- Every parser is tested against saved real responses in `tests/HushMusic.InnerTube.Tests/Fixtures`, with the
  expected values (produced by ytmusicapi itself on the same files) in `Fixtures/EXPECTED.md`.
- Every network call takes a `CancellationToken`, and nothing blocks the UI thread.

## Playback

1. A track is resolved lazily, right before it plays. `YtDlpStreamResolver` runs the app-managed yt-dlp
   (`-f "bestaudio[ext=webm]/bestaudio[acodec=opus]/bestaudio/best" -j --no-playlist`, with Deno as the JavaScript
   runtime) and parses its JSON. The next track in the queue and the track under the pointer are resolved ahead of
   time.
2. Stream links expire after about 6 hours. They are cached in memory and on disk until shortly before then. On HTTP
   403 the resolver re-resolves once and retries.
3. `MediaPlayerService` wraps `Windows.Media.Playback.MediaPlayer` and `SmtcController` drives the system media
   controls.
4. The queue lives in Core (`QueueService`), not in `MediaPlaybackList`, so features can read and change it.

yt-dlp and Deno are installed by the app into `%LOCALAPPDATA%\HushMusic\tools\<tool>\<version>\` from their official
GitHub releases, checked against the published SHA-256, and replaced side by side when a new version comes out (a
version folder that a running process still uses is cleaned up later).

## Extending Hush

Features are ordinary `IHostedService`s that use Core services and subscribe to their events. The built-in ones are the
templates to copy:

- [`PlayHistoryReporter`](../src/HushMusic.Core/Features/PlayHistoryReporter.cs) reports each started track to the
  YouTube Music history.
- [`QueueAutoExtender`](../src/HushMusic.Core/Features/QueueAutoExtender.cs) keeps radio and up-next queues topped up.
- `PlaybackSessionKeeper` (resume where you left off), `VolumeNormalizer`, `SleepTimer` and `LastFmService` show
  larger examples.

Hooks available to a feature:

| Service | What you get |
|---|---|
| `IPlayer` | `StatusChanged`, `TrackChanged`, `TrackStarted`, `TrackCompleted`, `PositionChanged`, `PlaybackFailed`; play, pause, seek, next, previous, volume, shuffle, repeat, `PauseAtEndOfTrack`, `FadeOutAndPauseAsync`, `IsLive` |
| `IQueueService` | the whole queue (read, insert, move, remove, shuffle, repeat), `Changed`, `CurrentChanged`, `GetSnapshot` / `Restore` |
| `IPlaybackActions` | play a track with up-next, start radio, play an album or playlist, add to queue, play next |
| `IAccountActionsService` | like and unlike; create, edit and delete playlists; add and remove items; raises `TrackRated` and `PlaylistChanged` |
| `IBrowseApi` / `ISearchApi` / `ILibraryApi` / `IWatchApi` | read-only YouTube Music data |
| `IAuthService` | sign-in state and `StatusChanged` |
| `ISleepTimer` | start, end of track, cancel; `Changed` |
| `ILastFmService` | Last.fm connection state, user name, pending scrobbles |
| `IRadioNowPlaying` | the song on air for live radio (ICY metadata) |
| `INotificationService` | show an InfoBar message |
| `IThemeService`, `IAccentColorService`, `IWindowModeService` (App) | theme and accent changes; mini player; show the window |

Events are raised on background threads; marshal to the UI with `IUiDispatcher` in the App. Register a feature with
`services.AddHostedService<MyFeature>()`, in `AddHushCore()` or in an extension method of its own.

## Design system

Hush uses its own flat, minimal design (Light, Dark and Auto) instead of the stock Windows look: no Mica, no stock
`TitleBar` or `NavigationView`. It has a custom title band, a 72 px icon rail with instant labels, a search modal over
a blurred backdrop, a floating glass player bar and a full-window Now Playing view.

- `src/HushMusic.App/Resources/Theme.xaml` holds the tokens: palette (in `ThemeDictionaries`, Dark and Light), the Inter
  type ramp, spacing, radii, glass (in-app acrylic) and the accent brushes.
- `Resources/Styles.xaml` holds the shared component styles: icon buttons, pill buttons, the toggle switch, tooltips.
- `Resources/ShellResources.xaml` and `Resources/PageResources.xaml` hold the shell's and the pages' templates.
- Use the keys rather than hard-coded values:
  - palette and glass through `{ThemeResource}`, because they switch live with the theme;
  - accent fills (buttons, selected chips, the toggle track) use `AccentBrush` with `AccentForegroundBrush` on top,
    through `{StaticResource}`, because they are recoloured in place;
  - accent text, glyphs, thin strokes and selection washes use `AccentTextBrush` and `AccentTintBrush` through
    `{ThemeResource}`: there is one per theme, each tuned to be legible on that theme's surfaces (so accent text inside
    an always-dark view stays readable when the app is light);
  - a subtree that sits on album art (Now Playing, mini player) sets `RequestedTheme="Dark"`.
- `IThemeService` applies Light, Dark or Auto. `IAccentColorService` takes the accent from the current album art, a
  preset (`AccentPresets`: Crimson, Forest, Frost, Ember, Iris) or the Windows accent colour, tuned per theme for
  legibility.
- `AppSettings.PlayerLayout` switches between the Standard player (floating glass bar, Cover Flow) and Minimal (a docked
  bar with the essentials and a "…" menu; Now Playing shows the single cover). It applies live.
- The typeface is [Inter](https://github.com/rsms/inter) (bundled, SIL OFL 1.1). Icons are Segoe Fluent Icons from
  Windows.

## Windows integration

- **Single instance.** `Program.Main` (with `DISABLE_XAML_GENERATED_MAIN`) redirects a second launch of the same
  install to the running copy.
- **Tray and close-to-tray.** `TrayIcon` (Shell_NotifyIcon with a native menu).
- **Taskbar thumbnail buttons and progress.** `TaskbarButtons` and `TaskbarList` (ITaskbarList3).
- **Taskbar player.** A layered Win32 window on its own thread, parented into `Shell_TrayWnd` and drawn with GDI+,
  plus a WinUI flyout window. Windows has no API for this, so it is opt-in and documented as fragile.
- **Updates.** Velopack (`Services/Updates`), with GitHub Releases as the feed.
