# Third-party notices

Hush is licensed under the GNU General Public License v3.0 (see [LICENSE](LICENSE)). It builds on the work below.
Each project keeps its own licence; this file lists them and says how each one is used.

## Shipped with the app

These are compiled into Hush or included in the installer.

| Component | Licence | Used for |
|---|---|---|
| [.NET runtime](https://github.com/dotnet/runtime) | MIT | The app runs on .NET 10, included in the installer (self-contained). |
| [Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK) | Microsoft Software License Terms (redistributable runtime); source on GitHub under MIT | UI framework, included in the installer (self-contained). The licence terms are in the `Microsoft.WindowsAppSDK` NuGet package. |
| [Microsoft.Web.WebView2](https://www.nuget.org/packages/Microsoft.Web.WebView2) | BSD-3-Clause style (Microsoft) | The Google sign-in window. The WebView2 runtime itself is part of Windows. |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | MIT | View models (source generators). |
| [Microsoft.Extensions.*](https://github.com/dotnet/runtime) (Hosting, DependencyInjection, Http, Logging, Options) | MIT | Dependency injection, hosting, `HttpClient` factory, options. |
| [Serilog](https://github.com/serilog/serilog), [Serilog.Extensions.Hosting](https://github.com/serilog/serilog-extensions-hosting), [Serilog.Extensions.Logging](https://github.com/serilog/serilog-extensions-logging), [Serilog.Sinks.File](https://github.com/serilog/serilog-sinks-file) | Apache-2.0 | The rolling log file. |
| [Velopack](https://github.com/velopack/velopack) | MIT | Installer and in-app updates. |
| [Inter](https://github.com/rsms/inter) (font) | SIL Open Font License 1.1 | The app's typeface. `InterVariable.ttf` ships in `Assets/Fonts`, with its licence in `Assets/Fonts/Inter-LICENSE.txt`. |

The MIT-licensed .NET components above are Copyright (c) .NET Foundation and Contributors / Microsoft Corporation.
The Apache License 2.0 text is at <https://www.apache.org/licenses/LICENSE-2.0>.

Icons in the interface come from **Segoe Fluent Icons** and **Segoe MDL2 Assets**, fonts that are part of Windows. Hush
uses them from the system and does not ship them.

## Downloaded at run time (not bundled)

Hush downloads these from their official GitHub releases on first run, checks each download's SHA-256 against the
checksum the project publishes, and keeps them in `%LOCALAPPDATA%\HushMusic\tools\`. They run as separate programs.

| Component | Licence | Used for |
|---|---|---|
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) | The Unlicense (public domain) | Finds the audio stream URL for a track. |
| [Deno](https://github.com/denoland/deno) | MIT (Copyright 2018-2026 the Deno authors) | The JavaScript runtime yt-dlp needs to solve YouTube's playback challenges. |

## Used for testing only (not shipped)

| Component | Licence |
|---|---|
| [xunit.v3](https://github.com/xunit/xunit), [xunit.runner.visualstudio](https://github.com/xunit/visualstudio.xunit) | Apache-2.0 |
| [Microsoft.NET.Test.Sdk](https://github.com/microsoft/vstest) | MIT |

## Reference implementation: ytmusicapi

Hush contains no code from [ytmusicapi](https://github.com/sigma67/ytmusicapi), but its InnerTube requests and response
parsers are modelled closely on it: endpoints, request bodies, headers, the client context and the paths used to read
responses follow ytmusicapi's source. The specs in [docs/innertube-requests.md](docs/innertube-requests.md) and
[docs/parsers-spec.md](docs/parsers-spec.md) cite the ytmusicapi functions they describe. ytmusicapi's licence:

```
MIT License

Copyright (c) 2026 sigma67

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Velopack licence

```
Copyright © 2021 Caelan Sayler
Copyright © 2024 Velopack Ltd.

Permission is hereby granted,  free of charge,  to any person obtaining a
copy of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to  use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

## Data and services

- **YouTube Music.** Hush talks to YouTube Music's private web API (InnerTube), the same one the official web app
  uses. Hush is not affiliated with, endorsed or sponsored by Google LLC or YouTube. "YouTube" and "YouTube Music" are
  trademarks of Google LLC. Music, artwork, lyrics and metadata belong to their owners.
- **[Radio Browser](https://www.radio-browser.info).** The radio search and the "Popular on Radio Browser" rows come
  from this free, community-maintained station directory, used through its public API as it asks (a descriptive
  User-Agent, one "click" per played station). Station names, logos and streams belong to their broadcasters.
- **[Last.fm](https://www.last.fm/api).** Scrobbling uses the Last.fm API with an API key you create yourself, under
  Last.fm's API terms.
