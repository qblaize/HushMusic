using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Lyrics;

/// <summary>
/// <see cref="ILyricsService"/>: YouTube Music, the video's captions and LRCLIB, asked in the user's order and only
/// when lyrics are shown. Each source is asked at most once per track at a time; what it answers is cached
/// (<see cref="LyricsCache"/>). Live radio never reaches any source.
/// </summary>
public sealed partial class LyricsService : ILyricsService
{
    /// <summary>A source that takes longer than this is given up on (and asked again next time).</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<LyricsService> _logger;
    private readonly LyricsCache _cache;
    private readonly Dictionary<LyricsProviderId, ILyricsProvider> _providers;
    private readonly Lock _gate = new();
    private readonly Dictionary<(string VideoId, LyricsProviderId Provider), Task<LyricsProviderResult>> _inFlight = [];
    private IReadOnlyList<LyricsSourceSetting> _order;

    public LyricsService(
        ISettingsService settings,
        IWatchApi watch,
        ILyricsBrowseIdSource browseIds,
        ICaptionLyricsApi captions,
        LrcLibClient lrcLib,
        IAppPaths paths,
        TimeProvider time,
        ILogger<LyricsService> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(paths);
        _settings = settings;
        _time = time;
        _logger = logger;
        _cache = new LyricsCache(Path.Combine(paths.Cache, "lyrics"), time, logger);
        _providers = new ILyricsProvider[]
        {
            new YouTubeLyricsProvider(watch, browseIds),
            new CaptionLyricsProvider(captions),
            new LrcLibLyricsProvider(lrcLib),
        }.ToDictionary(p => p.Id);
        _order = LyricsSources.Merge(settings.Current.LyricsSources);
        settings.Changed += (_, _) => OnOrderMaybeChanged();
    }

    public event EventHandler? SourceOrderChanged;

    public IReadOnlyList<LyricsSourceInfo> Sources => LyricsSources.All;

    public IReadOnlyList<LyricsSourceSetting> GetSourceOrder() => LyricsSources.Merge(_settings.Current.LyricsSources);

    public async Task SetSourceOrderAsync(IReadOnlyList<LyricsSourceSetting> order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        var merged = LyricsSources.Merge(order);
        await _settings.UpdateAsync(s => s.LyricsSources = [.. merged], cancellationToken).ConfigureAwait(false);
        OnOrderMaybeChanged();
    }

    public async Task<LyricsDocument?> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.IsLiveRadio)
        {
            return null;
        }

        var asked = new HashSet<LyricsProviderId>();
        var failed = new Dictionary<LyricsProviderId, Exception>();
        foreach (var setting in GetSourceOrder().Where(s => s.Enabled))
        {
            var provider = _providers[LyricsSources.ProviderOf(setting.Key)];
            if (!provider.Supports(track) || failed.ContainsKey(provider.Id))
            {
                continue;
            }

            asked.Add(provider.Id);
            LyricsProviderResult result;
            try
            {
                result = await GetResultAsync(track, provider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                LogSourceFailed(_logger, provider.Id, track.VideoId, ex);
                failed[provider.Id] = ex;
                continue;
            }

            if (result.Find(setting.Key) is { } document)
            {
                return document;
            }
        }

        if (asked.Count > 0 && failed.Count == asked.Count)
        {
            var first = failed.Values.First();
            throw first as HushException ?? new HushException("Couldn't load lyrics. Check your connection and try again.", first);
        }

        return null;
    }

    public async Task<LyricsDocument?> GetLyricsAsync(Track track, string sourceKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.IsLiveRadio || LyricsSources.Find(sourceKey) is not { } source)
        {
            return null;
        }

        var provider = _providers[LyricsSources.ProviderOf(source.Key)];
        if (!provider.Supports(track))
        {
            return null;
        }

        try
        {
            var result = await GetResultAsync(track, provider, cancellationToken).ConfigureAwait(false);
            return result.Find(source.Key);
        }
        catch (Exception ex) when (ex is not HushException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            throw new HushException($"Couldn't load lyrics from {source.ProviderName}.", ex);
        }
    }

    public async Task<IReadOnlyList<LyricsSourceInfo>> GetAvailableSourcesAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.IsLiveRadio)
        {
            return [];
        }

        var results = await _cache.GetAllAsync(track.VideoId, cancellationToken).ConfigureAwait(false);
        return
        [
            .. GetSourceOrder()
                .Where(s => s.Enabled
                    && results.TryGetValue(LyricsSources.ProviderOf(s.Key), out var result)
                    && result.Find(s.Key) is not null)
                .Select(s => LyricsSources.Find(s.Key)!),
        ];
    }

    /// <summary>The source's cached answer, or the one request in flight for it, or a new request.</summary>
    private async Task<LyricsProviderResult> GetResultAsync(Track track, ILyricsProvider provider, CancellationToken cancellationToken)
    {
        if (await _cache.GetAsync(track.VideoId, provider.Id, cancellationToken).ConfigureAwait(false) is { } cached)
        {
            return cached;
        }

        Task<LyricsProviderResult> fetch;
        lock (_gate)
        {
            var key = (track.VideoId, provider.Id);
            if (!_inFlight.TryGetValue(key, out fetch!))
            {
                fetch = FetchAsync(track, provider);
                _inFlight[key] = fetch;
            }
        }

        // The request itself isn't cancelled with the caller (a track change): its answer is still worth caching.
        return await fetch.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<LyricsProviderResult> FetchAsync(Track track, ILyricsProvider provider)
    {
        // Never completes synchronously, so the caller registers the task before the finally block removes it.
        await Task.Yield();
        try
        {
            using var timeout = new CancellationTokenSource(FetchTimeout, _time);
            var documents = await provider.FetchAsync(track, timeout.Token).ConfigureAwait(false);
            var result = new LyricsProviderResult(_time.GetUtcNow(), documents);
            LogFetched(_logger, provider.Id, track.VideoId, documents.Count);
            await _cache.StoreAsync(track.VideoId, provider.Id, result).ConfigureAwait(false);
            return result;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove((track.VideoId, provider.Id));
            }
        }
    }

    private void OnOrderMaybeChanged()
    {
        var order = GetSourceOrder();
        lock (_gate)
        {
            if (LyricsSources.SameOrder(order, _order))
            {
                return;
            }

            _order = order;
        }

        SourceOrderChanged?.Invoke(this, EventArgs.Empty);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Lyrics source {Provider} answered {Count} document(s) for {VideoId}")]
    private static partial void LogFetched(ILogger logger, LyricsProviderId provider, string videoId, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lyrics source {Provider} failed for {VideoId}")]
    private static partial void LogSourceFailed(ILogger logger, LyricsProviderId provider, string videoId, Exception exception);
}
