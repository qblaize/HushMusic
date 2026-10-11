using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.Core.Radio;

namespace HushMusic.Core.Features;

/// <summary>
/// <see cref="IRadioAutoSave"/>: follows the song on air (<see cref="IRadioNowPlaying"/>) and the player, and once a song
/// on a station with saving turned on has played for <see cref="RadioAutoSaveRules.MinimumAirtime"/>, matches it on
/// YouTube Music (<see cref="IRadioTrackMatcher"/>) and adds a confident match to the station's private "Heard on …"
/// playlist, made on first use. Saves run one at a time, in the background; each song is tried at most twice a session.
/// </summary>
/// <remarks>
/// A playlist stored in an earlier session is looked up in the library once before its first use: YouTube Music answers
/// an add to a deleted playlist with the same failed status as an add of a duplicate, so the add alone can't tell.
/// </remarks>
public sealed class RadioAutoSaver(
    RadioAutoSaveStore store,
    IPlayer player,
    IRadioNowPlaying radio,
    IRadioTrackMatcher matcher,
    IAccountActionsService account,
    ILibraryApi library,
    IAuthService auth,
    INotificationService notifications,
    TimeProvider time,
    ILogger<RadioAutoSaver> logger) : IRadioAutoSave, IHostedService, IDisposable
{
    /// <summary>A failed save is tried once more after this.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(1);

    // Pages of library playlists read when checking that a stored playlist still exists (about 25 per page).
    private const int MaxLibraryPages = 20;

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly RadioAirtimeTracker _airtime = new(RadioAutoSaveRules.MinimumAirtime);
    private readonly RadioAutoSaveLedger _ledger = new();
    private readonly HashSet<string> _verifiedPlaylists = new(StringComparer.Ordinal);
    private RadioStation? _station;
    private ITimer? _timer;

    public event EventHandler? Changed
    {
        add => store.Changed += value;
        remove => store.Changed -= value;
    }

    public event EventHandler<RadioSongSavedEventArgs>? SongSaved;

    public bool IsEnabled(string stationId) => store.IsEnabled(stationId);

    public Task LoadAsync(CancellationToken cancellationToken = default) => store.LoadAsync(cancellationToken);

    public Task SetEnabledAsync(RadioStation station, bool enabled, CancellationToken cancellationToken = default) =>
        store.SetEnabledAsync(station, enabled, cancellationToken);

    public bool WasSaved(string stationId, string streamTitle)
    {
        lock (_gate)
        {
            return _ledger.WasSaved(stationId, streamTitle);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _timer = time.CreateTimer(_ => OnTimer(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        player.TrackChanged += OnTrackChanged;
        player.StatusChanged += OnStatusChanged;
        radio.Changed += OnNowPlayingChanged;
        account.PlaylistChanged += OnPlaylistChanged;
        auth.StatusChanged += OnAuthStatusChanged;
        store.Changed += OnChoicesChanged;
        _ = LoadChoicesAsync();

        SyncPlayer();
        if (radio.Current is { } current)
        {
            OnSong(current.StationId, current);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        player.TrackChanged -= OnTrackChanged;
        player.StatusChanged -= OnStatusChanged;
        radio.Changed -= OnNowPlayingChanged;
        account.PlaylistChanged -= OnPlaylistChanged;
        auth.StatusChanged -= OnAuthStatusChanged;
        store.Changed -= OnChoicesChanged;
        _stopping.Cancel();
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private async Task LoadChoicesAsync()
    {
        try
        {
            await store.LoadAsync(_stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read which radio stations save their songs");
        }
    }

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e) => SyncPlayer();

    private void OnStatusChanged(object? sender, PlaybackStatusChangedEventArgs e) => SyncPlayer();

    private void OnNowPlayingChanged(object? sender, RadioNowPlayingChangedEventArgs e) => OnSong(e.StationId, e.NowPlaying);

    private void OnChoicesChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            Arm();
        }
    }

    private void OnAuthStatusChanged(object? sender, AuthStatusChangedEventArgs e)
    {
        lock (_gate)
        {
            // Another account (or none): stored playlists have to prove they belong to the account that signs in.
            if (e.Status != AuthStatus.SignedIn)
            {
                _verifiedPlaylists.Clear();
            }

            Arm();
        }
    }

    private void OnPlaylistChanged(object? sender, PlaylistChangedEventArgs e)
    {
        if (e.Kind != PlaylistChangeKind.Deleted || store.FindStationByPlaylist(e.PlaylistId) is not { } stationId)
        {
            return;
        }

        lock (_gate)
        {
            _verifiedPlaylists.Remove(RadioAutoSaveStore.WithoutBrowsePrefix(e.PlaylistId));
        }

        _ = ForgetPlaylistAsync(stationId);
    }

    private async Task ForgetPlaylistAsync(string stationId)
    {
        try
        {
            await store.SetPlaylistAsync(stationId, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not forget the deleted playlist of station {StationId}", stationId);
        }
    }

    private void SyncPlayer()
    {
        var station = player.CurrentTrack?.Station;
        var playing = player.Status == PlaybackStatus.Playing ? station : null;
        lock (_gate)
        {
            if (station is not null)
            {
                _station = station;
            }

            _airtime.SetPlaying(playing?.Id, time.GetUtcNow());
            Arm();
        }
    }

    private void OnSong(string stationId, RadioNowPlaying? song)
    {
        lock (_gate)
        {
            _airtime.SetSong(stationId, song, time.GetUtcNow());
            Arm();
        }
    }

    // Under _gate. Times only what could be saved: a song on a station that saves, while signed in.
    private void Arm()
    {
        var song = _airtime.Song;
        var due = song is not null && auth.Status == AuthStatus.SignedIn && store.IsEnabled(song.StationId)
            ? _airtime.DueIn(time.GetUtcNow())
            : null;
        _timer?.Change(due ?? Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer()
    {
        RadioNowPlaying? song;
        RadioStation? station;
        lock (_gate)
        {
            song = _airtime.TakeDue(time.GetUtcNow());
            station = _station;
            if (song is null)
            {
                Arm(); // fired a little early
                return;
            }
        }

        if (station?.Id == song.StationId)
        {
            _ = SaveAsync(station, song, retry: false);
        }
    }

    private async Task SaveAsync(RadioStation station, RadioNowPlaying song, bool retry)
    {
        var key = RadioAutoSaveRules.SongKey(song, station.Name);
        if (key.Length == 0 || RadioAutoSaveRules.IsAnnouncement(song))
        {
            logger.LogDebug("\"{Title}\" on {Station} is not a song; not saving it", song.StreamTitle, station.Name);
            return;
        }

        var token = _stopping.Token;
        try
        {
            await _work.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            // Checked again here: the station may have been turned off, or the account signed out, while waiting.
            if (!store.IsEnabled(station.Id) || auth.Status != AuthStatus.SignedIn)
            {
                return;
            }

            lock (_gate)
            {
                if (!(retry ? _ledger.TryRetry(station.Id, key) : _ledger.TryBegin(station.Id, key)))
                {
                    return;
                }
            }

            await SaveCoreAsync(station, song, key, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (AuthRequiredException ex)
        {
            lock (_gate)
            {
                _ledger.Skip(station.Id, key);
            }

            logger.LogDebug(ex, "Signed out; not saving \"{Title}\" from {Station}", song.StreamTitle, station.Name);
        }
        catch (Exception ex)
        {
            bool again;
            lock (_gate)
            {
                again = _ledger.Fail(station.Id, key);

                // The playlist may have been deleted meanwhile: the retry checks it again.
                if (store.Get(station.Id)?.PlaylistId is { } playlistId)
                {
                    _verifiedPlaylists.Remove(RadioAutoSaveStore.WithoutBrowsePrefix(playlistId));
                }
            }

            if (again)
            {
                logger.LogWarning(ex, "Could not save \"{Title}\" from {Station}; trying once more in {Delay}", song.StreamTitle, station.Name, RetryDelay);
                _ = RetryAsync(station, song);
            }
            else
            {
                logger.LogWarning(ex, "Could not save \"{Title}\" from {Station}; giving up on it", song.StreamTitle, station.Name);
            }
        }
        finally
        {
            _work.Release();
        }
    }

    private async Task RetryAsync(RadioStation station, RadioNowPlaying song)
    {
        try
        {
            await Task.Delay(RetryDelay, time, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await SaveAsync(station, song, retry: true).ConfigureAwait(false);
    }

    private async Task SaveCoreAsync(RadioStation station, RadioNowPlaying song, string key, CancellationToken cancellationToken)
    {
        var candidates = await matcher.FindAsync(song, station.Name, RadioAutoSaveRules.MatchCandidates, cancellationToken).ConfigureAwait(false);
        if (RadioAutoSaveRules.PickMatch(song, candidates, station.Name) is not { } match)
        {
            lock (_gate)
            {
                _ledger.Skip(station.Id, key);
            }

            logger.LogDebug("No sure match for \"{Title}\" from {Station}; not saving it", song.StreamTitle, station.Name);
            return;
        }

        bool known;
        lock (_gate)
        {
            known = _ledger.HasVideo(station.Id, match.VideoId);
        }

        // The same song announced another way is in the playlist already.
        var playlistId = known
            ? store.Get(station.Id)?.PlaylistId ?? string.Empty
            : await AddToStationPlaylistAsync(station, match.VideoId, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _ledger.Saved(station.Id, key, match.VideoId, song.StreamTitle);
        }

        logger.LogDebug("Saved \"{Title}\" from {Station} as {VideoId}", song.StreamTitle, station.Name, match.VideoId);
        SongSaved?.Invoke(this, new RadioSongSavedEventArgs(station.Id, song, match, playlistId));
    }

    private async Task<string> AddToStationPlaylistAsync(RadioStation station, string videoId, CancellationToken cancellationToken)
    {
        var playlistId = store.Get(station.Id)?.PlaylistId;
        if (playlistId is not null && !IsVerified(playlistId))
        {
            if (await PlaylistExistsAsync(playlistId, cancellationToken).ConfigureAwait(false))
            {
                MarkVerified(playlistId);
            }
            else
            {
                logger.LogInformation("The playlist for songs heard on {Station} is gone; making a new one", station.Name);
                playlistId = null;
            }
        }

        if (playlistId is null)
        {
            var title = RadioAutoSaveRules.PlaylistTitle(station.Name);
            playlistId = await account.CreatePlaylistAsync(title, RadioAutoSaveRules.PlaylistDescription(station.Name), PrivacyStatus.Private, [videoId], cancellationToken)
                .ConfigureAwait(false);
            MarkVerified(playlistId);
            logger.LogInformation("Made the playlist \"{Playlist}\" for songs heard on {Station}", title, station.Name);
            try
            {
                await store.SetPlaylistAsync(station.Id, playlistId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (HushException ex)
            {
                // Still used for the rest of the session; only the next session would make another one.
                logger.LogWarning(ex, "Could not remember the playlist made for {Station}", station.Name);
            }

            notifications.ShowInfo($"Saving songs from {station.Name}", $"Songs you hear on it go to your private playlist “{title}”.");
            return playlistId;
        }

        try
        {
            await account.AddToPlaylistAsync(playlistId, [videoId], allowDuplicates: false, cancellationToken).ConfigureAwait(false);
        }
        catch (AlreadyInPlaylistException)
        {
            logger.LogDebug("{VideoId} is already in the playlist of {Station}", videoId, station.Name);
        }

        return playlistId;
    }

    /// <summary>
    /// False only when the whole list of the user's playlists was read and it isn't in it. An empty list or one cut short
    /// keeps the stored playlist: making a duplicate every session would be worse than missing a deleted one.
    /// </summary>
    private async Task<bool> PlaylistExistsAsync(string playlistId, CancellationToken cancellationToken)
    {
        var id = RadioAutoSaveStore.WithoutBrowsePrefix(playlistId);
        string? continuation = null;
        var pages = 0;
        var any = false;
        do
        {
            var page = await library.GetPlaylistsAsync(continuation, cancellationToken).ConfigureAwait(false);
            foreach (var playlist in page.Items)
            {
                any = true;
                if (RadioAutoSaveStore.WithoutBrowsePrefix(playlist.PlaylistId) == id)
                {
                    return true;
                }
            }

            continuation = page.Continuation;
        }
        while (continuation is not null && ++pages < MaxLibraryPages);

        return continuation is not null || !any;
    }

    private bool IsVerified(string playlistId)
    {
        lock (_gate)
        {
            return _verifiedPlaylists.Contains(RadioAutoSaveStore.WithoutBrowsePrefix(playlistId));
        }
    }

    private void MarkVerified(string playlistId)
    {
        lock (_gate)
        {
            _verifiedPlaylists.Add(RadioAutoSaveStore.WithoutBrowsePrefix(playlistId));
        }
    }
}

public static class RadioAutoSaveServiceCollectionExtensions
{
    /// <summary>"Save songs to a playlist" for live stations: the stored choices and the feature that saves.</summary>
    public static IServiceCollection AddRadioAutoSave(this IServiceCollection services)
    {
        services.AddSingleton<RadioAutoSaveStore>();
        services.AddSingleton<RadioAutoSaver>();
        services.AddSingleton<IRadioAutoSave>(sp => sp.GetRequiredService<RadioAutoSaver>());
        services.AddHostedService(sp => sp.GetRequiredService<RadioAutoSaver>());
        return services;
    }
}
