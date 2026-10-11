using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Models;
using HushMusic.Core.Radio;
using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class RadioAirtimeTrackerTests
{
    private static readonly TimeSpan Threshold = TimeSpan.FromSeconds(30);
    private static readonly DateTimeOffset Start = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reports_a_song_once_after_it_played_for_the_threshold()
    {
        var tracker = new RadioAirtimeTracker(Threshold);
        tracker.SetPlaying("a", Start);
        tracker.SetSong("a", Song("a", "Artist - Title"), Start);

        Assert.Equal(Threshold, tracker.DueIn(Start));
        Assert.Null(tracker.TakeDue(At(29)));
        Assert.Equal("Artist - Title", tracker.TakeDue(At(30))?.StreamTitle);
        Assert.Null(tracker.TakeDue(At(90)));
        Assert.Null(tracker.DueIn(At(90)));
    }

    [Fact]
    public void Counts_only_the_time_the_station_actually_plays()
    {
        var tracker = new RadioAirtimeTracker(Threshold);
        tracker.SetSong("a", Song("a", "Artist - Title"), Start);
        tracker.SetPlaying("a", At(5)); // connected 5 s later
        tracker.SetPlaying(null, At(20)); // paused after 15 s
        Assert.Null(tracker.DueIn(At(20)));

        tracker.SetPlaying("a", At(100));
        Assert.Equal(TimeSpan.FromSeconds(15), tracker.DueIn(At(100)));
        Assert.Null(tracker.TakeDue(At(114)));
        Assert.NotNull(tracker.TakeDue(At(115)));
    }

    [Fact]
    public void Zapping_to_another_station_saves_nothing()
    {
        var tracker = new RadioAirtimeTracker(Threshold);
        tracker.SetPlaying("a", Start);
        tracker.SetSong("a", Song("a", "Artist - Title"), Start);
        tracker.SetPlaying("b", At(10));

        Assert.Null(tracker.Song);
        Assert.Null(tracker.TakeDue(At(60)));

        // Back on the first station: its title has to be heard again from the start.
        tracker.SetPlaying("a", At(60));
        tracker.SetSong("a", Song("a", "Artist - Title"), At(61));
        Assert.Null(tracker.TakeDue(At(90)));
        Assert.NotNull(tracker.TakeDue(At(91)));
    }

    [Fact]
    public void A_new_title_starts_over_but_new_artwork_does_not()
    {
        var tracker = new RadioAirtimeTracker(Threshold);
        tracker.SetPlaying("a", Start);
        tracker.SetSong("a", Song("a", "Jingle"), Start);
        tracker.SetSong("a", Song("a", "Artist - Title"), At(20));
        tracker.SetSong("a", Song("a", "Artist - Title") with { ArtworkUrl = "https://example.com/cover.jpg" }, At(35));

        Assert.Null(tracker.TakeDue(At(49)));
        var due = tracker.TakeDue(At(50));
        Assert.Equal("Artist - Title", due?.StreamTitle);
        Assert.Equal("https://example.com/cover.jpg", due?.ArtworkUrl);
    }

    [Fact]
    public void An_ident_or_a_break_clears_the_song_but_another_station_clearing_does_not()
    {
        var tracker = new RadioAirtimeTracker(Threshold);
        tracker.SetPlaying("a", Start);
        tracker.SetSong("a", Song("a", "Artist - Title"), Start);
        tracker.SetSong("b", null, At(10));
        Assert.NotNull(tracker.Song);

        tracker.SetSong("a", null, At(20));
        Assert.Null(tracker.Song);
        Assert.Null(tracker.TakeDue(At(60)));
    }

    [Fact]
    public void A_song_from_a_station_that_is_not_playing_is_not_timed()
    {
        var tracker = new RadioAirtimeTracker(Threshold);
        tracker.SetSong("a", Song("a", "Artist - Title"), Start);

        Assert.Null(tracker.DueIn(At(60)));
        Assert.Null(tracker.TakeDue(At(60)));
    }

    private static DateTimeOffset At(int seconds) => Start.AddSeconds(seconds);

    private static RadioNowPlaying Song(string stationId, string streamTitle)
    {
        var separator = streamTitle.IndexOf(" - ", StringComparison.Ordinal);
        return separator > 0
            ? new RadioNowPlaying(stationId, streamTitle, streamTitle[..separator], streamTitle[(separator + 3)..], null)
            : new RadioNowPlaying(stationId, streamTitle, null, streamTitle, null);
    }
}

public sealed class RadioAutoSaveRulesTests
{
    [Theory]
    [InlineData("Avicii", "Levels", "Levels", "Avicii", true)]
    [InlineData("Avicii", "Levels (Radio Edit)", "Levels", "Avicii", true)]
    [InlineData("Artist", "Title", "Title (feat. Singer)", "Artist", true)]
    [InlineData("Artist", "Title", "Title - Radio Edit", "Artist", true)]
    [InlineData("Artist feat. Singer", "Title", "Title", "Singer", true)]
    [InlineData("Ștefan Bănică", "Titlu", "Titlu", "Stefan Banica", true)]
    [InlineData("Artist", "Title", "Title", "Karaoke Band", false)]
    [InlineData("Artist", "Title", "Something Else", "Artist", false)]
    [InlineData("Artist", "Love Me", "Me", "Artist", false)]
    [InlineData("Artist", "Title (Purple Disco Machine Remix)", "Title", "Artist", true)]
    [InlineData("Various", "Title", "Title", "Someone", false)]
    [InlineData(null, "Daft Punk One More Time", "One More Time", "Daft Punk", true)]
    [InlineData(null, "One More Time", "One More Time", "Daft Punk", false)]
    public void Only_a_matching_title_and_artist_is_a_confident_match(string? artist, string title, string trackTitle, string trackArtist, bool confident)
    {
        var song = new RadioNowPlaying("a", artist is null ? title : $"{artist} - {title}", artist, title, null);

        Assert.Equal(confident, RadioTrackMatcher.IsConfident(song, Track("v", trackTitle, trackArtist)));
    }

    [Fact]
    public void Picks_the_first_confident_match_among_the_top_three()
    {
        var song = new RadioNowPlaying("a", "Artist - Title", "Artist", "Title", null);
        var cover = Track("cover", "Title", "Karaoke Band");
        var real = Track("real", "Title", "Artist");
        var later = Track("later", "Title", "Artist");

        Assert.Equal("real", RadioAutoSaveRules.PickMatch(song, [cover, real, later], null)?.VideoId);
        Assert.Null(RadioAutoSaveRules.PickMatch(song, [cover, cover, cover, real], null));
        Assert.Null(RadioAutoSaveRules.PickMatch(song, [], null));
    }

    [Theory]
    [InlineData(null, "Werbung", true)]
    [InlineData(null, "ADVERTISEMENT", true)]
    [InlineData("Ad Break", "Spot 3", true)]
    [InlineData("Various", "News", true)]
    [InlineData("Three Days Grace", "Break", false)]
    [InlineData("Artist", "Title", false)]
    [InlineData(null, "Breakfast Show", false)]
    public void Recognises_breaks_and_announcements(string? artist, string title, bool expected)
    {
        var song = new RadioNowPlaying("a", artist is null ? title : $"{artist} - {title}", artist, title, null);

        Assert.Equal(expected, RadioAutoSaveRules.IsAnnouncement(song));
    }

    [Fact]
    public void The_same_song_announced_differently_has_one_key()
    {
        var plain = new RadioNowPlaying("a", "Avicii - Levels", "Avicii", "Levels", null);
        var edit = new RadioNowPlaying("a", "AVICII - Levels (Radio Edit)", "AVICII", "Levels (Radio Edit)", null);
        var empty = new RadioNowPlaying("a", "[NEW]", null, "[NEW]", null);

        Assert.Equal(RadioAutoSaveRules.SongKey(plain, "Test FM"), RadioAutoSaveRules.SongKey(edit, "Test FM"));
        Assert.Equal(string.Empty, RadioAutoSaveRules.SongKey(empty, "Test FM"));
    }

    [Fact]
    public void Names_the_playlist_after_the_station()
    {
        Assert.Equal("Heard on Test FM", RadioAutoSaveRules.PlaylistTitle("  Test   FM "));
        Assert.Equal("Heard on Radio X", RadioAutoSaveRules.PlaylistTitle("Radio <X>"));
        Assert.Equal("Heard on the radio", RadioAutoSaveRules.PlaylistTitle(""));
        Assert.Equal(150, RadioAutoSaveRules.PlaylistTitle(new string('x', 400)).Length);
    }

    [Fact]
    public void The_ledger_tries_a_song_once_and_retries_a_failure_once()
    {
        var ledger = new RadioAutoSaveLedger();

        Assert.True(ledger.TryBegin("a", "song"));
        Assert.False(ledger.TryBegin("a", "song")); // in progress
        Assert.True(ledger.TryBegin("b", "song")); // another station has its own playlist
        Assert.False(ledger.TryRetry("a", "song")); // nothing failed

        Assert.True(ledger.Fail("a", "song"));
        Assert.False(ledger.TryBegin("a", "song")); // aired again while waiting: the retry covers it
        Assert.True(ledger.TryRetry("a", "song"));
        Assert.False(ledger.Fail("a", "song")); // second failure: given up
        Assert.False(ledger.TryRetry("a", "song"));
        Assert.False(ledger.TryBegin("a", "song"));

        ledger.Saved("b", "song", "v1", "Artist - Song");
        Assert.True(ledger.HasVideo("b", "v1"));
        Assert.False(ledger.HasVideo("a", "v1"));
        Assert.True(ledger.WasSaved("b", "Artist - Song"));
        Assert.False(ledger.TryBegin("b", "song"));
    }

    private static Track Track(string videoId, string title, string artist) => new()
    {
        Title = title,
        VideoId = videoId,
        Artists = [new ArtistRef(artist, null)],
    };
}

public sealed class RadioAutoSaveStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hush-autosave-tests-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Keeps_the_choice_and_the_playlist_across_a_restart()
    {
        var store = new RadioAutoSaveStore(new AppPaths(_root), NullLogger<RadioAutoSaveStore>.Instance);
        var changes = 0;
        store.Changed += (_, _) => changes++;
        await store.LoadAsync(Ct);

        var station = RadioData.Station("a", "A FM");
        await store.SetEnabledAsync(station, true, Ct);
        await store.SetEnabledAsync(station, true, Ct); // no change
        await store.SetEnabledAsync(RadioData.Station("b", "B FM"), false, Ct); // never on: nothing stored
        await store.SetPlaylistAsync("a", "PL1", Ct);
        await store.SetEnabledAsync(station, false, Ct);

        Assert.Equal(2, changes);
        Assert.False(store.IsEnabled("a"));
        Assert.Equal("PL1", store.Get("a")?.PlaylistId);
        Assert.Null(store.Get("b"));
        Assert.Equal("a", store.FindStationByPlaylist("VLPL1"));

        var reloaded = new RadioAutoSaveStore(new AppPaths(_root), NullLogger<RadioAutoSaveStore>.Instance);
        await reloaded.LoadAsync(Ct);
        Assert.Equal(new RadioAutoSaveEntry("a", "A FM", false, "PL1"), reloaded.Get("a"));

        await reloaded.SetEnabledAsync(station, true, Ct);
        Assert.True(reloaded.IsEnabled("a"));
        Assert.Equal("PL1", reloaded.Get("a")?.PlaylistId); // turning it back on keeps the playlist
    }

    [Fact]
    public async Task An_unreadable_file_starts_with_every_station_off()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, RadioAutoSaveStore.FileName), "{ not json", Ct);
        var store = new RadioAutoSaveStore(new AppPaths(_root), NullLogger<RadioAutoSaveStore>.Instance);

        await store.LoadAsync(Ct);

        Assert.False(store.IsEnabled("a"));
    }
}

public sealed class RadioAutoSaverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hush-autosaver-tests-" + Guid.NewGuid().ToString("N"));
    private readonly RadioStation _station = RadioData.Station("a", "Test FM");
    private readonly FakePlayer _player = new();
    private readonly AutoSaveFakeRadio _radio = new();
    private readonly RadioMatchFakeSearch _search = new();
    private readonly AutoSaveFakeAccount _account = new();
    private readonly AutoSaveFakeLibrary _library = new();
    private readonly FakeAuth _auth = new();
    private readonly FakeNotifications _notifications = new();
    private readonly ManualTimeProvider _time = new();
    private readonly RadioAutoSaveStore _store;
    private readonly RadioAutoSaver _saver;
    private readonly List<RadioSongSavedEventArgs> _saved = [];

    public RadioAutoSaverTests()
    {
        _store = new RadioAutoSaveStore(new AppPaths(_root), NullLogger<RadioAutoSaveStore>.Instance);
        _saver = new RadioAutoSaver(
            _store,
            _player,
            _radio,
            new RadioTrackMatcher(_search, NullLogger<RadioTrackMatcher>.Instance),
            _account,
            _library,
            _auth,
            _notifications,
            _time,
            NullLogger<RadioAutoSaver>.Instance);
        _saver.SongSaved += (_, e) =>
        {
            lock (_saved)
            {
                _saved.Add(e);
            }
        };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _saver.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Saves_a_song_that_played_for_30_seconds_to_a_new_private_playlist()
    {
        await StartAsync(enabled: true);
        Found("Artist", "Title", "v1");

        OnAir("Artist - Title");
        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty(_search.Calls);
        _time.Advance(TimeSpan.FromSeconds(1));

        await Until(() => _store.Get("a")?.PlaylistId == "PLnew1" && SavedCount == 1);
        var created = Assert.Single(_account.Created);
        Assert.Equal("Heard on Test FM", created.Title);
        Assert.Equal(PrivacyStatus.Private, created.Privacy);
        Assert.Equal(["v1"], created.VideoIds);
        Assert.Empty(_account.Added);
        Assert.True(_saver.WasSaved("a", "Artist - Title"));
        await Until(() => _notifications.Shown.Count == 1);
        Assert.Equal("PLnew1", _saved[0].PlaylistId);
    }

    [Fact]
    public async Task Adds_later_songs_to_the_same_playlist_and_never_the_same_song_twice()
    {
        await StartAsync(enabled: true, playlistId: "PL1");
        _library.Playlists.Add(Playlist("PL1"));
        Found("Artist", "Title", "v1");
        Found("Other", "Song", "v2");

        Hear("Artist - Title");
        Hear("Other - Song");
        Hear("Artist - Title (Radio Edit)");
        Hear("Artist - Title");

        await Until(() => SavedCount == 2);
        Assert.Equal([("PL1", "v1"), ("PL1", "v2")], _account.Added);
        Assert.Empty(_account.Created);
        Assert.Single(_library.Calls); // the stored playlist is checked once a session
        Assert.Equal(2, _search.Calls.Count);
        Assert.Empty(_notifications.Shown);
    }

    [Fact]
    public async Task A_song_already_in_the_playlist_is_skipped_quietly()
    {
        await StartAsync(enabled: true, playlistId: "PL1");
        _library.Playlists.Add(Playlist("PL1"));
        Found("Artist", "Title", "v1");
        _account.AddFailure = id => new AlreadyInPlaylistException(id, ["v1"]);

        Hear("Artist - Title");

        await Until(() => SavedCount == 1);
        Assert.True(_saver.WasSaved("a", "Artist - Title"));
        Assert.Empty(_notifications.Shown);
        Assert.Empty(_account.Created);
    }

    [Fact]
    public async Task Makes_a_new_playlist_when_the_stored_one_was_deleted()
    {
        await StartAsync(enabled: true, playlistId: "PLgone");
        _library.Playlists.Add(Playlist("LM"));
        Found("Artist", "Title", "v1");

        Hear("Artist - Title");

        await Until(() => _store.Get("a")?.PlaylistId == "PLnew1");
        Assert.Empty(_account.Added);
        Assert.Single(_account.Created);
    }

    [Fact]
    public async Task Keeps_the_stored_playlist_when_the_library_reads_empty()
    {
        await StartAsync(enabled: true, playlistId: "PL1");
        Found("Artist", "Title", "v1");

        Hear("Artist - Title");

        await Until(() => SavedCount == 1);
        Assert.Equal([("PL1", "v1")], _account.Added);
        Assert.Empty(_account.Created);
    }

    [Fact]
    public async Task Forgets_a_playlist_deleted_in_the_app()
    {
        await StartAsync(enabled: true, playlistId: "PL1");

        _account.RaiseDeleted("VLPL1");

        await Until(() => _store.Get("a")?.PlaylistId is null);
        Assert.True(_store.IsEnabled("a"));
    }

    [Fact]
    public async Task Does_nothing_for_a_station_that_is_off_or_while_signed_out()
    {
        await StartAsync(enabled: false);
        Found("Artist", "Title", "v1");
        Hear("Artist - Title");

        await _store.SetEnabledAsync(_station, true, Ct);
        _auth.Status = AuthStatus.SignedOut;
        Hear("Other - Song");

        Assert.Empty(_search.Calls);
        Assert.Empty(_account.Created);
        Assert.Empty(_account.Added);
    }

    [Fact]
    public async Task Short_songs_paused_stations_ads_and_unmatched_titles_save_nothing()
    {
        await StartAsync(enabled: true, playlistId: "PL1");
        _library.Playlists.Add(Playlist("PL1"));

        // A jingle: replaced before 30 s.
        OnAir("Station Jingle - Summer");
        _time.Advance(TimeSpan.FromSeconds(20));

        // Paused after 10 s: the rest doesn't count.
        OnAir("Artist - Title");
        _time.Advance(TimeSpan.FromSeconds(10));
        _player.RaiseStatusChanged(PlaybackStatus.Paused);
        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(_search.Calls);

        // An ad break, then a song nobody has on YouTube Music.
        _player.RaiseStatusChanged(PlaybackStatus.Playing);
        Hear("Werbung");
        Hear("Unknown Band - Unknown Song");

        Assert.Equal(["Unknown Band Unknown Song"], _search.Calls.Select(c => c.Query).Distinct());
        Assert.Empty(_account.Added);
        Assert.Equal(0, SavedCount);
    }

    [Fact]
    public async Task A_failed_save_is_retried_once_then_given_up()
    {
        await StartAsync(enabled: true, playlistId: "PL1");
        _library.Playlists.Add(Playlist("PL1"));
        Found("Artist", "Title", "v1");
        Found("Other", "Song", "v2");
        _account.AddFailure = _ => new InnerTubeException("browse/edit_playlist", "offline");

        Hear("Artist - Title");
        Assert.Single(_account.AddAttempts);

        _time.Advance(RadioAutoSaver.RetryDelay);
        await Until(() => _account.AddAttempts.Count == 2);

        _time.Advance(RadioAutoSaver.RetryDelay * 3);
        Assert.Equal(2, _account.AddAttempts.Count);

        // A later song still saves once the account works again.
        _account.AddFailure = null;
        Hear("Other - Song");
        await Until(() => SavedCount == 1);
        Assert.Equal([("PL1", "v2")], _account.Added);
        Assert.Empty(_notifications.Shown);
    }

    private int SavedCount
    {
        get
        {
            lock (_saved)
            {
                return _saved.Count;
            }
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private static Playlist Playlist(string id) => new() { Title = id, PlaylistId = id };

    private async Task StartAsync(bool enabled, string? playlistId = null)
    {
        await _store.LoadAsync(Ct);
        if (enabled || playlistId is not null)
        {
            await _store.SetEnabledAsync(_station, true, Ct);
            await _store.SetPlaylistAsync(_station.Id, playlistId, Ct);
            await _store.SetEnabledAsync(_station, enabled, Ct);
        }

        await _saver.StartAsync(Ct);
        _player.CurrentTrack = _station.ToTrack();
        _player.RaiseTrackChanged(_player.CurrentTrack);
        _player.RaiseStatusChanged(PlaybackStatus.Playing);
    }

    private void Found(string artist, string title, string videoId)
    {
        var existing = _search.Results.TryGetValue(SearchFilter.Songs, out var results) ? results.AllItems.OfType<Track>() : [];
        var track = new Track { Title = title, VideoId = videoId, Artists = [new ArtistRef(artist, null)] };
        _search.Results[SearchFilter.Songs] = new SearchResults { Sections = [new Shelf { Title = "Songs", Items = [track, .. existing] }] };
    }

    private void OnAir(string streamTitle) => _radio.Raise(IcyMetadata.ToNowPlaying(_station, streamTitle));

    // On air for longer than the threshold.
    private void Hear(string streamTitle)
    {
        OnAir(streamTitle);
        _time.Advance(TimeSpan.FromSeconds(31));
    }
}

internal sealed class AutoSaveFakeRadio : IRadioNowPlaying
{
    public RadioNowPlaying? Current { get; private set; }

    public event EventHandler<RadioNowPlayingChangedEventArgs>? Changed;

    public void Raise(RadioNowPlaying? song)
    {
        Current = song;
        Changed?.Invoke(this, new RadioNowPlayingChangedEventArgs(song?.StationId ?? "a", song));
    }

    public void Follow(RadioStation station)
    {
    }

    public void Unfollow(bool forget)
    {
    }
}

internal sealed class AutoSaveFakeLibrary : ILibraryApi
{
    public List<Playlist> Playlists { get; } = [];

    public List<string?> Calls { get; } = [];

    public Task<Paged<Playlist>> GetPlaylistsAsync(string? continuation = null, CancellationToken cancellationToken = default)
    {
        Calls.Add(continuation);
        return Task.FromResult(new Paged<Playlist>([.. Playlists], null));
    }

    public Task<Paged<Track>> GetSongsAsync(string? continuation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Paged<Album>> GetAlbumsAsync(string? continuation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Paged<Artist>> GetArtistsAsync(string? continuation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Paged<Track>> GetLikedSongsAsync(string? continuation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<Shelf>> GetHistoryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class AutoSaveFakeAccount : IAccountActionsService
{
    private readonly Lock _gate = new();

    public List<(string Title, string? Description, PrivacyStatus Privacy, IReadOnlyList<string> VideoIds)> Created { get; } = [];

    /// <summary>Adds that went through.</summary>
    public List<(string PlaylistId, string VideoId)> Added { get; } = [];

    public List<string> AddAttempts { get; } = [];

    /// <summary>Makes adds fail with the returned exception (null: they succeed).</summary>
    public Func<string, Exception>? AddFailure { get; set; }

    public event EventHandler<TrackRatedEventArgs>? TrackRated { add { } remove { } }

    public event EventHandler<PlaylistChangedEventArgs>? PlaylistChanged;

    public void RaiseDeleted(string playlistId) => PlaylistChanged?.Invoke(this, new PlaylistChangedEventArgs(playlistId, PlaylistChangeKind.Deleted, []));

    public Task<string> CreatePlaylistAsync(string title, string? description, PrivacyStatus privacy, IReadOnlyList<string>? videoIds = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Created.Add((title, description, privacy, videoIds ?? []));
            return Task.FromResult("PLnew" + Created.Count);
        }
    }

    public Task AddToPlaylistAsync(string playlistId, IReadOnlyList<string> videoIds, bool allowDuplicates = false, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            AddAttempts.Add(playlistId);
            if (AddFailure is { } failure)
            {
                return Task.FromException(failure(playlistId));
            }

            Added.AddRange(videoIds.Select(id => (playlistId, id)));
            return Task.CompletedTask;
        }
    }

    public Task RateTrackAsync(string videoId, LikeStatus status, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<string>> AddMissingToPlaylistAsync(string playlistId, IReadOnlyList<string> videoIds, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task RemoveFromPlaylistAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task MovePlaylistItemAsync(string playlistId, Track track, Track? successor, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<Track>> RestorePlaylistItemsAsync(string playlistId, IReadOnlyList<RemovedPlaylistEntry> entries, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task EditPlaylistAsync(string playlistId, string? title = null, string? description = null, PrivacyStatus? privacy = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
