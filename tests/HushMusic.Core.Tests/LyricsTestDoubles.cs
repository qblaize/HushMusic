using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Lyrics;
using HushMusic.Core.Models;

namespace HushMusic.Core.Tests;

/// <summary>Watch API with lyrics per browse id; counts lyrics calls.</summary>
internal sealed class LyricsFakeWatchApi : IWatchApi
{
    private int _lyricsCalls;

    public Dictionary<string, Models.Lyrics?> Lyrics { get; } = [];

    public Exception? Failure { get; set; }

    public int LyricsCalls => Volatile.Read(ref _lyricsCalls);

    /// <summary>Lets a test hold the lyrics call open.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<Models.Lyrics?> GetLyricsAsync(string lyricsBrowseId, bool timestamps = false, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _lyricsCalls);
        if (Gate is { } gate)
        {
            await gate.Task.WaitAsync(cancellationToken);
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        return Lyrics.GetValueOrDefault(lyricsBrowseId);
    }

    public Task<WatchPlaylist> GetWatchPlaylistAsync(string? videoId, string? playlistId = null, bool radio = false, bool shuffle = false, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<Paged<Track>> GetWatchPlaylistContinuationAsync(string continuation, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Shelf>> GetRelatedAsync(string relatedBrowseId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<double?> GetLoudnessDbAsync(string videoId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>Lyrics browse ids per video ("MPLY" + video id unless set otherwise).</summary>
internal sealed class LyricsFakeBrowseIds : ILyricsBrowseIdSource
{
    public Dictionary<string, string?> Ids { get; } = [];

    public Task<string?> GetLyricsBrowseIdAsync(string videoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Ids.TryGetValue(videoId, out var id) ? id : "MPLY" + videoId);
}

internal sealed class LyricsFakeCaptions : ICaptionLyricsApi
{
    private int _calls;

    public Dictionary<string, CaptionLyrics?> Captions { get; } = [];

    public Exception? Failure { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public Task<CaptionLyrics?> GetCaptionLyricsAsync(string videoId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        return Failure is { } failure ? Task.FromException<CaptionLyrics?>(failure) : Task.FromResult(Captions.GetValueOrDefault(videoId));
    }
}

/// <summary>Scripted LRCLIB: answers by request path ("/api/get", "/api/search"); records every request.</summary>
internal sealed class LyricsFakeLrcLib : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<HttpRequestMessage> _requests = [];

    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => NotFound();

    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage NotFound() =>
        Json("""{"code":404,"name":"TrackNotFound","message":"Failed to find specified track"}""", HttpStatusCode.NotFound);

    /// <summary>/api/get answers <paramref name="get"/> (404 when null), /api/search answers <paramref name="search"/>.</summary>
    public void Answer(string? get, string search = "[]") =>
        Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/api/get" => get is null ? NotFound() : Json(get),
            "/api/search" => Json(search),
            _ => NotFound(),
        };

    public LrcLibClient CreateClient() => new(new Factory(this), NullLogger<LrcLibClient>.Instance);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        return Task.FromResult(Respond(request));
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            var client = new HttpClient(handler, disposeHandler: false);
            LrcLibClient.Configure(client);
            return client;
        }
    }
}

internal sealed class LyricsFakeSettings : ISettingsService
{
    public AppSettings Current { get; } = new();

    public event EventHandler? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default)
    {
        update(Current);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}

internal sealed class LyricsClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class LyricsTempPaths : IAppPaths, IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "hushmusic-lyrics-tests", Guid.NewGuid().ToString("N"));

    public string Logs => Ensure("logs");

    public string Cache => Ensure("cache");

    public string ImageCache => Ensure(Path.Combine("cache", "images"));

    public string Secure => Ensure("secure");

    public string Tools => Ensure("tools");

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string LyricsFolder => Path.Combine(Cache, "lyrics");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Ensure(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>A lyrics service wired to fakes, sharing one cache folder and clock across <see cref="CreateService"/> calls.</summary>
internal sealed class LyricsTestHost : IDisposable
{
    public LyricsFakeSettings Settings { get; } = new();

    public LyricsFakeWatchApi Watch { get; } = new();

    public LyricsFakeBrowseIds BrowseIds { get; } = new();

    public LyricsFakeCaptions Captions { get; } = new();

    public LyricsFakeLrcLib LrcLib { get; } = new();

    public LyricsTempPaths Paths { get; } = new();

    public LyricsClock Clock { get; } = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));

    public LyricsService CreateService() => new(
        Settings,
        Watch,
        BrowseIds,
        Captions,
        LrcLib.CreateClient(),
        Paths,
        Clock,
        NullLogger<LyricsService>.Instance);

    public void Dispose() => Paths.Dispose();
}
