using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features.Stats;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features;

/// <summary>
/// <see cref="IWeeklyMix"/>: builds "Fresh for you" from the play log (<see cref="WeeklyMixPlanner"/>) and keeps it in
/// <c>weekly-mix.json</c> under the app folder, so Home shows it without waiting.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The first check runs a minute after startup; later ones only when a song starts (no timer runs in between), so a
/// copy left running in the notification area still gets its Monday mix.</item>
/// <item>A build makes at most <see cref="MaxRequests"/> requests, one after another: a radio for each seed song and the
/// page of each seed artist (for their top songs), then the radios' next pages while the mix is short.</item>
/// <item>A failed build is tried again an hour later; without enough history, six hours later.</item>
/// <item>A song that becomes often skipped (<see cref="ISmartSkip"/>) drops out of the mix on show right away; the saved
/// mix keeps it, so forgiving the song brings it back.</item>
/// </list>
/// </remarks>
public sealed partial class WeeklyMixService : IWeeklyMix, IHostedService, IDisposable
{
    public const string FileName = "weekly-mix.json";

    /// <summary>The most InnerTube requests one build makes.</summary>
    public const int MaxRequests = 15;

    /// <summary>A mix with fewer songs than this isn't worth showing; the previous one stays.</summary>
    public const int MinimumSize = 10;

    private const int FormatVersion = 1;

    internal static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryWithoutHistory = TimeSpan.FromHours(6);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IgnoreReadOnlyProperties = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IPlayLog _playLog;
    private readonly IWatchApi _watchApi;
    private readonly IBrowseApi _browseApi;
    private readonly IPlayer _player;
    private readonly IAppPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<WeeklyMixService> _logger;
    private readonly ISmartSkip? _smartSkip;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();

    // Guarded by _gate.
    private Task? _load;
    private Task _build = Task.CompletedTask;
    private bool _ready;
    private bool _building;
    private long _retryAfter;

    // The mix as built (or read back), and as shown: without the songs smart skip leaves out.
    private volatile WeeklyMix? _current;
    private volatile WeeklyMix? _visible;

    public WeeklyMixService(
        IPlayLog playLog,
        IWatchApi watchApi,
        IBrowseApi browseApi,
        IPlayer player,
        IAppPaths paths,
        TimeProvider time,
        ILogger<WeeklyMixService> logger,
        ISmartSkip? smartSkip = null)
    {
        _playLog = playLog;
        _watchApi = watchApi;
        _browseApi = browseApi;
        _player = player;
        _paths = paths;
        _time = time;
        _logger = logger;
        _smartSkip = smartSkip;
    }

    public event EventHandler? Changed;

    public WeeklyMix? Current => _visible;

    /// <summary>The pause between two requests of a build: it runs in the background and is never in a hurry.</summary>
    internal TimeSpan RequestGap { get; init; } = TimeSpan.FromMilliseconds(250);

    private string FilePath => Path.Combine(_paths.Root, FileName);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _player.TrackStarted += OnTrackStarted;
        if (_smartSkip is not null)
        {
            _smartSkip.Changed += OnSmartSkipChanged;
        }

        _ = FirstCheckAsync();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _player.TrackStarted -= OnTrackStarted;
        if (_smartSkip is not null)
        {
            _smartSkip.Changed -= OnSmartSkipChanged;
        }

        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _stopping.Dispose();

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_gate)
        {
            load = _load ??= Task.Run(ReadAsync, CancellationToken.None);
        }

        return load.WaitAsync(cancellationToken);
    }

    /// <summary>Builds now if a new mix is due. Returns the build (completed when none was started). Tests call it directly.</summary>
    internal Task CheckAsync()
    {
        lock (_gate)
        {
            if (_building || _stopping.IsCancellationRequested || _time.GetTimestamp() < _retryAfter
                || !WeeklyMixPlanner.IsDue(_current?.BuiltAt, _time.GetLocalNow(), _time.LocalTimeZone))
            {
                return _build;
            }

            _building = true;
            _build = Task.Run(BuildAndKeepAsync);
            return _build;
        }
    }

    private async Task FirstCheckAsync()
    {
        try
        {
            await Task.Delay(StartDelay, _time, _stopping.Token).ConfigureAwait(false);
            await LoadAsync(_stopping.Token).ConfigureAwait(false);
            lock (_gate)
            {
                _ready = true;
            }

            await CheckAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
    }

    private void OnTrackStarted(object? sender, TrackChangedEventArgs e)
    {
        bool ready;
        lock (_gate)
        {
            ready = _ready;
        }

        if (ready)
        {
            _ = CheckAsync();
        }
    }

    private async Task BuildAndKeepAsync()
    {
        var retry = TimeSpan.Zero;
        try
        {
            var tracks = await BuildAsync(_stopping.Token).ConfigureAwait(false);
            if (tracks is null)
            {
                retry = RetryWithoutHistory;

                // The history no longer supports a mix (e.g. the statistics were cleared): stop showing the old one.
                if (_current is not null)
                {
                    File.Delete(FilePath);
                    Show(null);
                }

                return;
            }

            if (tracks.Count < MinimumSize)
            {
                LogTooFew(_logger, tracks.Count);
                retry = RetryWithoutHistory;
                return;
            }

            var mix = new WeeklyMix(_time.GetLocalNow(), tracks);
            await WriteAsync(mix).ConfigureAwait(false);
            Show(mix);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not build the weekly mix");
            retry = RetryAfterFailure;
        }
        finally
        {
            lock (_gate)
            {
                _building = false;
                _retryAfter = retry > TimeSpan.Zero ? _time.GetTimestamp() + (long)(retry.TotalSeconds * _time.TimestampFrequency) : 0;
            }
        }
    }

    /// <summary>The new mix's songs, or null when there isn't enough listening history.</summary>
    private async Task<List<Track>?> BuildAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetLocalNow();
        var seeds = WeeklyMixPlanner.PickSeeds(await _playLog.ReadAsync(now - WeeklyMixPlanner.HistoryWindow, cancellationToken).ConfigureAwait(false), now);
        if (seeds is null)
        {
            LogNotEnoughHistory(_logger, WeeklyMixPlanner.MinimumPlays);
            return null;
        }

        var heard = HeardSongs.From(await _playLog.ReadAsync(null, cancellationToken).ConfigureAwait(false));

        var requests = 0;
        var failures = 0;
        var sources = new List<IReadOnlyList<Track>>();
        var radios = new List<Radio>();

        // A radio, then an artist's top songs, and so on: the merge takes from each source in turn.
        for (var i = 0; i < Math.Max(seeds.Songs.Count, seeds.Artists.Count); i++)
        {
            var seed = i < seeds.Songs.Count ? seeds.Songs[i] : null;
            if (seed is not null && await RequestAsync(ct => _watchApi.GetWatchPlaylistAsync(seed, null, radio: true, shuffle: false, ct)) is { } watch)
            {
                var radio = new Radio([.. watch.Tracks.Where(t => t.VideoId != seed)], watch.Continuation);
                radios.Add(radio);
                sources.Add(radio.Tracks);
            }

            if (i < seeds.Artists.Count && await RequestAsync(ct => _browseApi.GetArtistAsync(seeds.Artists[i].BrowseId!, ct)) is { } artist)
            {
                sources.Add(artist.TopSongs);
            }
        }

        if (requests > 0 && failures == requests)
        {
            throw new HushException("None of the songs for the weekly mix could be loaded.");
        }

        var tracks = Merge();

        // Short of songs: the radios' next pages, while requests are left.
        while (tracks.Count < WeeklyMixPlanner.TargetSize && requests < MaxRequests && radios.Exists(r => r.Continuation is not null))
        {
            foreach (var radio in radios.Where(r => r.Continuation is not null))
            {
                if (requests >= MaxRequests)
                {
                    break;
                }

                var continuation = radio.Continuation!;
                radio.Continuation = null;
                if (await RequestAsync(ct => _watchApi.GetWatchPlaylistContinuationAsync(continuation, ct)) is { } page)
                {
                    radio.Tracks.AddRange(page.Items);
                    radio.Continuation = page.Continuation;
                }
            }

            tracks = Merge();
        }

        LogBuilt(_logger, tracks.Count, requests, seeds.Plays);
        return [.. tracks.Select(ForStorage)];

        List<Track> Merge() =>
            WeeklyMixPlanner.Merge(sources, heard, videoId => _smartSkip?.IsLeftOut(videoId) == true, Random.Shared);

        // One request at a time, a little apart.
        async Task<T?> RequestAsync<T>(Func<CancellationToken, Task<T>> request)
            where T : class
        {
            if (requests >= MaxRequests)
            {
                return null;
            }

            if (requests++ > 0 && RequestGap > TimeSpan.Zero)
            {
                await Task.Delay(RequestGap, _time, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                return await request(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failures++;
                LogRequestFailed(_logger, ex);
                return null;
            }
        }
    }

    // What the queue and the pages need; account-specific bits (like state, playlist entry ids) would go stale.
    private static Track ForStorage(Track track) => track with
    {
        LikeStatus = null,
        FeedbackTokens = null,
        SetVideoId = null,
        PlaylistId = null,
        Views = null,
    };

    private async Task ReadAsync()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            await using var stream = File.OpenRead(FilePath);
            var file = await JsonSerializer.DeserializeAsync<MixFile>(stream, JsonOptions).ConfigureAwait(false);
            List<Track> tracks = [.. file?.Tracks?.Where(t => t is not null && !string.IsNullOrWhiteSpace(t.VideoId)) ?? []];
            if (file is null || tracks.Count == 0)
            {
                return;
            }

            // A build may have finished first.
            if (_current is null)
            {
                Show(new WeeklyMix(file.BuiltAt, tracks));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "The weekly mix file is unreadable; a new mix is built");
        }
    }

    private void Show(WeeklyMix? mix)
    {
        lock (_gate)
        {
            _current = mix;
            _visible = WithoutLeftOut(mix);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnSmartSkipChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            var visible = WithoutLeftOut(_current);
            if (visible?.Tracks.Count == _visible?.Tracks.Count)
            {
                return;
            }

            _visible = visible;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private WeeklyMix? WithoutLeftOut(WeeklyMix? mix)
    {
        if (mix is null || _smartSkip is null)
        {
            return mix;
        }

        var tracks = _smartSkip.WithoutLeftOut(mix.Tracks);
        return tracks.Count == mix.Tracks.Count ? mix : mix with { Tracks = tracks };
    }

    private async Task WriteAsync(WeeklyMix mix)
    {
        var temp = FilePath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            var file = new MixFile(FormatVersion, mix.BuiltAt, mix.Tracks);
            await JsonSerializer.SerializeAsync(stream, file, JsonOptions, CancellationToken.None).ConfigureAwait(false);
        }

        File.Move(temp, FilePath, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Built the weekly mix: {Count} songs, {Requests} requests, from {Plays} plays")]
    private static partial void LogBuilt(ILogger logger, int count, int requests, int plays);

    [LoggerMessage(Level = LogLevel.Information, Message = "No weekly mix: fewer than {Plays} plays in the last eight weeks")]
    private static partial void LogNotEnoughHistory(ILogger logger, int plays);

    [LoggerMessage(Level = LogLevel.Information, Message = "No weekly mix: only {Count} new songs were found")]
    private static partial void LogTooFew(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "A request for the weekly mix failed")]
    private static partial void LogRequestFailed(ILogger logger, Exception exception);

    private sealed class Radio(List<Track> tracks, string? continuation)
    {
        public List<Track> Tracks { get; } = tracks;

        public string? Continuation { get; set; } = continuation;
    }

    private sealed record MixFile(int Version, DateTimeOffset BuiltAt, IReadOnlyList<Track>? Tracks);
}
