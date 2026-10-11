using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.NowPlaying;

/// <summary>
/// "Heard on air" in Now Playing while a live station plays: the songs this session heard on that station (its ICY
/// titles), newest first, the one on air now marked, and the station's "Save songs to a playlist" switch
/// (<see cref="IRadioAutoSave"/>), with a check on the songs it saved. Each station keeps its own list until the app
/// closes; the list itself isn't stored. Singleton, created with Now Playing at startup so it hears every title.
/// </summary>
public sealed partial class RadioHeardViewModel : ObservableObject
{
    private const int MaxPerStation = 100;

    private readonly IRadioNowPlaying _radio;
    private readonly IRadioAutoSave _autoSave;
    private readonly IAuthService _auth;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly Dictionary<string, List<HeardSongViewModel>> _history = new(StringComparer.Ordinal);
    private RadioStation? _station;
    private bool _syncingAutoSave;

    public RadioHeardViewModel(
        IRadioNowPlaying radio,
        IPlayer player,
        IRadioAutoSave autoSave,
        IAuthService auth,
        INotificationService notifications,
        IUiDispatcher dispatcher)
    {
        _radio = radio;
        _autoSave = autoSave;
        _auth = auth;
        _notifications = notifications;
        _dispatcher = dispatcher;
        radio.Changed += (_, e) => _dispatcher.Run(() => OnNowPlaying(e.StationId, e.NowPlaying));
        player.TrackChanged += (_, e) => _dispatcher.Run(() => ShowStation(e.Track?.Station));
        autoSave.Changed += (_, _) => _dispatcher.Run(SyncAutoSave);
        autoSave.SongSaved += (_, e) => _dispatcher.Run(() => MarkSaved(e.StationId, e.Song.StreamTitle));
        auth.StatusChanged += (_, _) => _dispatcher.Run(SyncAutoSave);
        _dispatcher.Run(() =>
        {
            ShowStation(player.CurrentTrack?.Station);
            if (radio.Current is { } current)
            {
                OnNowPlaying(current.StationId, current);
            }
        });
    }

    /// <summary>The playing station's songs, newest first.</summary>
    public ObservableCollection<HeardSongViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Caption), nameof(AutoSaveDescription), nameof(CanAutoSave))]
    public partial string? StationName { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    public string Caption => string.IsNullOrEmpty(StationName) ? "This session" : $"On {StationName} this session";

    /// <summary>"Save songs to a playlist" for the playing station (two-way bound to the switch).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoSaveDescription))]
    public partial bool IsAutoSaveOn { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutoSaveDescription), nameof(CanAutoSave))]
    public partial bool IsSignedIn { get; set; }

    /// <summary>Signed in, with a station playing: the switch can be used.</summary>
    public bool CanAutoSave => IsSignedIn && StationName is not null;

    public string AutoSaveDescription => this switch
    {
        { IsSignedIn: false } => "Sign in to save songs",
        { IsAutoSaveOn: true } => $"Songs that play for {RadioAutoSaveRules.MinimumAirtime.TotalSeconds:0} seconds go to “{RadioAutoSaveRules.PlaylistTitle(StationName ?? string.Empty)}”",
        _ => "Add the songs you hear here to a playlist on your account",
    };

    partial void OnIsAutoSaveOnChanged(bool value)
    {
        if (!_syncingAutoSave && _station is { } station)
        {
            _ = SetAutoSaveAsync(station, value);
        }
    }

    private void ShowStation(RadioStation? station)
    {
        if (station?.Id == _station?.Id)
        {
            return;
        }

        _station = station;
        StationName = station?.Name;
        Items.Clear();
        if (station is not null && _history.TryGetValue(station.Id, out var songs))
        {
            foreach (var song in songs)
            {
                Items.Add(song);
            }
        }

        MarkOnAir(station is null ? null : _radio.Current);
        IsEmpty = Items.Count == 0;
        SyncAutoSave();
    }

    private void SyncAutoSave()
    {
        _syncingAutoSave = true;
        try
        {
            IsSignedIn = _auth.Status == AuthStatus.SignedIn;
            IsAutoSaveOn = _station is not null && _autoSave.IsEnabled(_station.Id);
        }
        finally
        {
            _syncingAutoSave = false;
        }
    }

    private async Task SetAutoSaveAsync(RadioStation station, bool enabled)
    {
        try
        {
            await _autoSave.SetEnabledAsync(station, enabled);
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Couldn't change song saving", ex);
            SyncAutoSave();
        }
    }

    private void MarkSaved(string stationId, string streamTitle)
    {
        if (_history.TryGetValue(stationId, out var songs))
        {
            foreach (var song in songs.Where(s => string.Equals(s.Song.StreamTitle, streamTitle, StringComparison.Ordinal)))
            {
                song.IsSaved = true;
            }
        }
    }

    private void OnNowPlaying(string stationId, RadioNowPlaying? nowPlaying)
    {
        if (nowPlaying is not null)
        {
            Remember(stationId, nowPlaying);
        }

        if (stationId == _station?.Id)
        {
            MarkOnAir(nowPlaying);
        }
    }

    private void Remember(string stationId, RadioNowPlaying nowPlaying)
    {
        if (!_history.TryGetValue(stationId, out var songs))
        {
            songs = [];
            _history[stationId] = songs;
        }

        // The same song again right after a station ident or an ad break is still the same airing.
        if (songs.Count > 0 && string.Equals(songs[0].Song.StreamTitle, nowPlaying.StreamTitle, StringComparison.Ordinal))
        {
            return;
        }

        var song = new HeardSongViewModel(nowPlaying, DateTimeOffset.Now) { IsSaved = _autoSave.WasSaved(stationId, nowPlaying.StreamTitle) };
        songs.Insert(0, song);
        if (songs.Count > MaxPerStation)
        {
            songs.RemoveAt(songs.Count - 1);
        }

        if (stationId == _station?.Id)
        {
            Items.Insert(0, song);
            if (Items.Count > MaxPerStation)
            {
                Items.RemoveAt(Items.Count - 1);
            }

            IsEmpty = false;
        }
    }

    private void MarkOnAir(RadioNowPlaying? nowPlaying)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].IsOnAir = i == 0 && nowPlaying is not null && string.Equals(Items[i].Song.StreamTitle, nowPlaying.StreamTitle, StringComparison.Ordinal);
        }
    }
}

/// <summary>One song heard on a station.</summary>
public sealed partial class HeardSongViewModel(RadioNowPlaying song, DateTimeOffset heardAt) : ObservableObject
{
    public RadioNowPlaying Song { get; } = song;

    public string Title => Song.Title;

    public string Artist => Song.Artist ?? string.Empty;

    public bool HasArtist => !string.IsNullOrWhiteSpace(Song.Artist);

    /// <summary>Cover sent by the station with the title, if any.</summary>
    public string? ArtworkUrl => Song.ArtworkUrl;

    /// <summary>"14:05", in the user's time format.</summary>
    public string TimeText { get; } = heardAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    public string AutomationName => (HasArtist ? $"{Song.Title}, {Song.Artist}" : Song.Title) + (IsSaved ? ", saved to playlist" : string.Empty);

    /// <summary>The station is playing it right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotOnAir))]
    public partial bool IsOnAir { get; set; }

    public bool IsNotOnAir => !IsOnAir;

    /// <summary>"Save songs to a playlist" added it to the station's playlist.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationName))]
    public partial bool IsSaved { get; set; }
}
