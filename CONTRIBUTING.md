# Contributing to Hush

Thanks for your interest. Bug reports, fixes and small, focused features are all welcome. For anything bigger (a new
dependency, a change to how sign-in or playback works, a new page), please open an issue first so we can agree on the
approach before you spend time on it.

Before you start, read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md). It explains how the projects fit together and
where the unofficial parts live.

## Getting set up

- Windows 10 1809+ or Windows 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download) 10.0.201 or later (pinned in `global.json`, with roll-forward)
- Optional: Visual Studio 2026 with the WinUI application development workload, for XAML editing and F5 debugging

```powershell
git clone https://github.com/qblaize/HushMusic.git
cd HushMusic
dotnet build HushMusic.slnx
dotnet run --project src/HushMusic.App
```

In Visual Studio, open `HushMusic.slnx` and start the "HushMusic (Unpackaged)" profile. Development builds run
unpackaged and use the same data folder as an installed copy (`%LOCALAPPDATA%\HushMusic`). To keep your own data out
of the way, point a test instance somewhere else:

```powershell
$env:HUSHMUSIC_DATA_ROOT = "$env:TEMP\hush-dev"
dotnet run --project src/HushMusic.App
```

Only one copy of the app runs per install folder: a second launch brings the running window to the front. Close the
app before you rebuild, or the build can't overwrite its files.

## Tests

The tests use xUnit v3 on Microsoft.Testing.Platform (set in `global.json`). Run each test project:

```powershell
dotnet test --project tests/HushMusic.Core.Tests/HushMusic.Core.Tests.csproj
dotnet test --project tests/HushMusic.InnerTube.Tests/HushMusic.InnerTube.Tests.csproj
dotnet test --project tests/HushMusic.Playback.Tests/HushMusic.Playback.Tests.csproj
```

- **Parser changes need a fixture.** Parsers are tested on real saved InnerTube responses in
  `tests/HushMusic.InnerTube.Tests/Fixtures`. Capture new ones **anonymously** (no cookies, no account), save them
  minified, and replace every `visitorData` value and the `visitor_data` tracking parameter with `"REDACTED"`. Add the
  expected values to `Fixtures/EXPECTED.md`, ideally produced by running the matching ytmusicapi function on the same
  file.
- Pages that only exist when signed in (library, history, account menu) are covered by the hand-built files in
  `Fixtures/Synthetic`, assembled from anonymous captures. **Never commit a response captured with your own account:**
  it contains your name, library, history and identifiers.
- Core logic (queue, playback actions, features, radio, Last.fm) is tested with the fakes in
  `tests/HushMusic.Core.Tests/Fakes.cs`.

## Code conventions

- C# with nullable reference types on everywhere. Warnings are errors in `HushMusic.Core` and `HushMusic.InnerTube`.
- Formatting follows `.editorconfig` (file-scoped namespaces, four spaces, two for XML/XAML/JSON).
- MVVM with CommunityToolkit.Mvvm. Code-behind holds only UI glue; logic goes in view models and services.
- Async all the way, with a `CancellationToken` on every network call. Never block the UI thread; update the UI through
  the dispatcher (`IUiDispatcher`).
- InnerTube requests and parsers follow [ytmusicapi](https://github.com/sigma67/ytmusicapi). Don't invent endpoints
  or fields. If you deviate, say why in [docs/innertube-requests.md](docs/innertube-requests.md) or
  [docs/parsers-spec.md](docs/parsers-spec.md).
- Parsers are null-safe: a missing field logs a warning and skips the item. They never throw for the whole page.
- Keep YouTube-specific code inside `HushMusic.InnerTube` (API), `HushMusic.Playback` (stream resolution) and
  `HushMusic.Auth` (sign-in), behind the Core interfaces.
- UI: use the tokens in `Resources/Theme.xaml` and the styles in `Resources/Styles.xaml` instead of hard-coded colours,
  sizes or fonts (see the design system section of [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)).
- Comments explain why, especially where InnerTube or Windows does something non-obvious. Skip comments that repeat
  the code.
- Secrets never go in code or plain-text files. Cookies and keys are stored with DPAPI.
- Don't log stream URLs in full (they are IP-bound and signed), cookies, or tokens.

## Pull requests

- One topic per pull request, with a short description of what changed and how you tested it.
- `dotnet build` must pass without warnings in Core and InnerTube, and all tests must pass.
- For UI changes, add a before/after screenshot. Check both the Light and the Dark theme.
- Screenshots and logs must not show personal data: use a test instance that isn't signed in.

## Design decisions

These came out of testing against the real service, and explain choices that might look odd at first.

1. **Sign-in is cookie-based; there is no OAuth.** Since August 2025 InnerTube rejects every OAuth Bearer token with
   HTTP 400 ([ytmusicapi#813](https://github.com/sigma67/ytmusicapi/issues/813), still open). The WebView2 sign-in window
   is the main login and "Paste a cookie header" is the fallback. Requests are signed with SAPISIDHASH exactly as
   ytmusicapi does.
2. **yt-dlp is app-managed.** The app installs the official onedir build (`yt-dlp_win.zip`, checked against
   `SHA2-256SUMS`) into `tools\yt-dlp\<version>\`, and installs new releases itself, side by side, because yt-dlp's
   `-U` refuses onedir builds. The onedir build starts about 0.6 s faster per track than the single-file
   `yt-dlp.exe`, which unpacks itself on every run. yt-dlp older than 2026.08.19 can't resolve playable audio at all.
3. **yt-dlp needs a JavaScript runtime.** The app installs Deno (yt-dlp's recommended runtime) into
   `tools\deno\<version>\`. Until it's installed, a Node.js on PATH is used. Settings → Stream resolver can set any
   other `--js-runtimes` value.
4. **WebM/Opus first.** The format selector is
   `bestaudio[ext=webm]/bestaudio[acodec=opus]/bestaudio/best` with `--extractor-args youtube:skip=hls`. YouTube's M4A
   audio is fragmented (DASH) MP4: Windows MediaPlayer opens it over HTTP, then ends within half a second without
   playing anything.
5. **yt-dlp runs without the account by default.** yt-dlp warns that accounts used with it can be rate-limited or
   blocked, and anonymous resolution works for normal tracks. "Use my account when resolving streams" turns it on for
   age-restricted or account-only tracks.
6. **The one plain-text exception.** yt-dlp only reads cookies from a file, so when (5) is on it gets a Netscape cookie
   file readable only by the current user, deleted as soon as yt-dlp exits.
7. **Hiding yt-dlp's latency.** A run takes about 1.6 to 1.9 s, so the app pre-resolves the next track, the track under
   the pointer and the first track of each page; keeps resolved links (memory and disk) until they expire; and after a
   network error reopens the same link before running yt-dlp again.
8. **URL parameters.** Requests use `?alt=json&prettyPrint=false`: ytmusicapi's parameter plus compact output, which is
   60–70 % smaller.
9. **Endpoints beyond the basics,** all taken from ytmusicapi: `player` plus a ping to `s.youtube.com` (history),
   `account/account_menu` ("Signed in as"), `like/dislike` and `playlist/delete`.
10. **Two requests go without the account.** Timed lyrics (the Android client, as ytmusicapi's `as_mobile()`) fail with
    HTTP 400 when signed in ([ytmusicapi#828](https://github.com/sigma67/ytmusicapi/issues/828)), and an
    Android-client reply must never end the session. Track loudness (`player`) runs for every track, so it stays
    anonymous too.
11. **Session upkeep.** Rotated session cookies from `Set-Cookie` responses are saved, for existing youtube.com cookies
    only. ytmusicapi doesn't do this; it helps sessions last longer.
12. **Live radio never goes through yt-dlp.** Stations play their direct stream URL, and radio plays are kept out of
    YouTube history, Last.fm and loudness lookups.
13. **The taskbar player is opt-in.** It parents a window into Explorer's taskbar, which Windows doesn't support, so it
    can break with Windows updates.

## Releasing

Releases are built with [Velopack](https://github.com/velopack/velopack). Installed copies update themselves from the
latest GitHub release.

1. Bump `<Version>` in `Directory.Build.props` (it is the only version number: assemblies, Settings → About and the
   installer all read it).
2. Commit, then push a matching tag:

   ```powershell
   git tag v0.2.0
   git push origin v0.2.0
   ```

3. The [Release workflow](.github/workflows/release.yml) runs the tests, then `build/release.ps1 -Upload`, which
   publishes a self-contained x64 build, packs it with `vpk` and uploads `Setup.exe`, the portable zip and the update
   packages to a GitHub release named after the tag. The tag must match the version, or the build stops.

To build the installer locally without uploading:

```powershell
./build/release.ps1            # x64; add -Arch arm64 for ARM64
```

The output goes to `artifacts/releases/win-x64/` (git-ignored). `vpk` is a local tool pinned in
`.config/dotnet-tools.json`, restored by the script. Releases aren't code-signed.

## Reporting a security problem

If you find a way Hush could leak cookies or other credentials, please don't open a public issue. Use GitHub's
[private vulnerability reporting](https://github.com/qblaize/HushMusic/security/advisories/new) instead.

## Licence

By contributing, you agree that your contributions are licensed under the [GNU General Public License v3.0](LICENSE),
like the rest of Hush.
