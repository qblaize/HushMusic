using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features.NewReleases;
using HushMusic.Core.Models;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class NewReleasePlannerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_fresh_state_reads_the_followed_artists_first()
    {
        Assert.IsType<NewReleasePlanner.RefreshFollowed>(NewReleasePlanner.Next(new NewReleaseState(), T0));
    }

    [Fact]
    public void Never_checked_artists_come_first_then_the_least_recently_checked()
    {
        var state = Followed(T0, ("UCb", T0.AddHours(-30)), ("UCa", null), ("UCc", T0.AddHours(-25)));

        Assert.Equal("UCa", Assert.IsType<NewReleasePlanner.CheckArtist>(NewReleasePlanner.Next(state, T0)).ArtistId);
        state.Artists.Find(a => a.Id == "UCa")!.CheckedAt = T0;
        Assert.Equal("UCb", Assert.IsType<NewReleasePlanner.CheckArtist>(NewReleasePlanner.Next(state, T0)).ArtistId);
    }

    [Fact]
    public void Each_artist_is_checked_at_most_once_a_day_and_the_list_read_once_a_day()
    {
        var state = Followed(T0, ("UCa", T0.AddHours(-2)), ("UCb", T0.AddHours(-1)));

        var wait = Assert.IsType<NewReleasePlanner.WaitUntil>(NewReleasePlanner.Next(state, T0));
        Assert.Equal(T0.AddHours(22), wait.Until); // UCa is due again first

        Assert.IsType<NewReleasePlanner.RefreshFollowed>(NewReleasePlanner.Next(state, T0.AddHours(24)));
    }

    [Fact]
    public void The_daily_cap_spreads_a_long_list_over_several_days()
    {
        var state = Followed(T0, ("UCa", null), ("UCb", null), ("UCc", null));
        var page = Page("A", Album("MPREb_1", "One", "2026"));

        for (var i = 0; i < 2; i++)
        {
            var check = Assert.IsType<NewReleasePlanner.CheckArtist>(NewReleasePlanner.Next(state, T0.AddMinutes(i), maxChecksPerDay: 2));
            NewReleasePlanner.RecordCheck(state, check.ArtistId, page, T0.AddMinutes(i));
        }

        var wait = Assert.IsType<NewReleasePlanner.WaitUntil>(NewReleasePlanner.Next(state, T0.AddMinutes(5), maxChecksPerDay: 2));
        Assert.Equal(T0.AddHours(24), wait.Until);

        // The next day: the list first, then the artist left over.
        Assert.IsType<NewReleasePlanner.RefreshFollowed>(NewReleasePlanner.Next(state, T0.AddHours(24), maxChecksPerDay: 2));
        state.FollowedRefreshedAt = T0.AddHours(24);
        Assert.Equal("UCc", Assert.IsType<NewReleasePlanner.CheckArtist>(NewReleasePlanner.Next(state, T0.AddHours(24), maxChecksPerDay: 2)).ArtistId);
    }

    [Fact]
    public void The_first_check_of_an_artist_is_silent()
    {
        var state = Followed(T0, ("UCa", null));

        var found = NewReleasePlanner.RecordCheck(state, "UCa", Page("Daft Punk", Album("MPREb_1", "Homework", "2026")), T0);

        Assert.Empty(found);
        Assert.Empty(state.Pending);
        Assert.Contains("MPREb_1", state.Artists[0].Known!);
        Assert.Equal("Daft Punk", state.Artists[0].Name);
        Assert.Equal(1, state.ChecksInWindow);
    }

    [Fact]
    public void A_release_not_seen_before_is_new()
    {
        var state = Followed(T0, ("UCa", null));
        NewReleasePlanner.RecordCheck(state, "UCa", Page("Daft Punk", Album("MPREb_1", "Homework", "2025")), T0);

        var found = NewReleasePlanner.RecordCheck(
            state,
            "UCa",
            Page("Daft Punk", Album("MPREb_2", "Random Access Memories II", "2026", AlbumType.Album), Album("MPREb_1", "Homework", "2025")),
            T0.AddDays(1));

        var release = Assert.Single(found);
        Assert.Equal(("UCa", "Daft Punk", "MPREb_2", "Random Access Memories II", "2026"), (release.ArtistId, release.ArtistName, release.BrowseId, release.Title, release.Year));
        Assert.Single(state.Pending);
    }

    [Fact]
    public void Old_releases_that_reappear_are_recorded_but_not_announced()
    {
        var state = Followed(T0, ("UCa", null));
        NewReleasePlanner.RecordCheck(state, "UCa", Page("A", Album("MPREb_1", "New", "2026")), T0);

        var found = NewReleasePlanner.RecordCheck(state, "UCa", Page("A", Album("MPREb_1", "New", "2026"), Album("MPREb_9", "Old", "2019")), T0.AddDays(1));

        Assert.Empty(found);
        Assert.Contains("MPREb_9", state.Artists[0].Known!);
    }

    [Fact]
    public void A_release_that_only_changed_its_id_is_not_new()
    {
        var state = Followed(T0, ("UCa", null));
        NewReleasePlanner.RecordCheck(state, "UCa", Page("A", Album("MPREb_old", "Same Title", "2026", AlbumType.Single)), T0);

        var found = NewReleasePlanner.RecordCheck(state, "UCa", Page("A", Album("MPREb_new", "Same title", "2026", AlbumType.Single)), T0.AddDays(1));

        Assert.Empty(found);
    }

    [Fact]
    public void An_album_named_like_an_earlier_single_is_new()
    {
        var state = Followed(T0, ("UCa", null));
        NewReleasePlanner.RecordCheck(state, "UCa", Page("A", Album("MPREb_s", "Discovery", "2026", AlbumType.Single)), T0);

        var found = NewReleasePlanner.RecordCheck(state, "UCa", Page("A", Album("MPREb_a", "Discovery", "2026", AlbumType.Album)), T0.AddDays(1));

        Assert.Equal("MPREb_a", Assert.Single(found).BrowseId);
    }

    [Fact]
    public void A_page_without_releases_does_not_become_the_baseline()
    {
        var state = Followed(T0, ("UCa", null));
        NewReleasePlanner.RecordCheck(state, "UCa", Page("A"), T0);
        Assert.Null(state.Artists[0].Known);

        var found = NewReleasePlanner.RecordCheck(state, "UCa", Page("A", Album("MPREb_1", "Out now", "2026")), T0.AddDays(1));

        Assert.Empty(found); // still the first real look at the page
        Assert.NotNull(state.Artists[0].Known);
    }

    [Fact]
    public void After_a_long_gap_the_check_starts_over_silently()
    {
        var state = Followed(T0, ("UCa", null));
        NewReleasePlanner.RecordCheck(state, "UCa", Page("A", Album("MPREb_1", "One", "2026")), T0);

        var found = NewReleasePlanner.RecordCheck(state, "UCa", Page("A", Album("MPREb_2", "Two", "2026")), T0 + NewReleasePlanner.BaselineExpiry + TimeSpan.FromDays(1));

        Assert.Empty(found);
    }

    [Fact]
    public void A_release_shared_by_two_followed_artists_is_announced_once()
    {
        var state = Followed(T0, ("UCa", null), ("UCb", null));
        NewReleasePlanner.RecordCheck(state, "UCa", Page("A", Album("MPREb_0", "Zero", "2026")), T0);
        NewReleasePlanner.RecordCheck(state, "UCb", Page("B", Album("MPREb_0b", "Zero B", "2026")), T0);

        NewReleasePlanner.RecordCheck(state, "UCa", Page("A", Album("MPREb_duet", "Duet", "2026")), T0.AddDays(1));
        NewReleasePlanner.RecordCheck(state, "UCb", Page("B", Album("MPREb_duet", "Duet", "2026")), T0.AddDays(1));

        Assert.Equal("MPREb_duet", Assert.Single(state.Pending).BrowseId);
    }

    [Fact]
    public void Refreshing_the_list_keeps_what_is_known_and_forgets_unfollowed_artists()
    {
        var state = Followed(T0, ("UCa", T0), ("UCgone", T0));
        state.Artists[0].Known = ["MPREb_1"];

        NewReleasePlanner.SetFollowed(state, [Artist("UCa", "A renamed"), Artist("MPLAUCnew", "New"), Artist("UCa", "Duplicate")], T0.AddDays(1));

        Assert.Equal(["UCa", "UCnew"], state.Artists.Select(a => a.Id));
        Assert.Equal(["MPREb_1"], state.Artists[0].Known);
        Assert.Equal("A renamed", state.Artists[0].Name);
        Assert.Null(state.Artists[1].CheckedAt);
        Assert.Equal(T0.AddDays(1), state.FollowedRefreshedAt);
    }

    [Fact]
    public void Pending_releases_are_taken_once_and_stale_ones_dropped()
    {
        var state = new NewReleaseState();
        state.Pending.Add(Pending("MPREb_old", T0 - NewReleasePlanner.PendingExpiry - TimeSpan.FromHours(1)));
        state.Pending.Add(Pending("MPREb_new", T0));

        var taken = NewReleasePlanner.TakePending(state, T0);

        Assert.Equal("MPREb_new", Assert.Single(taken).BrowseId);
        Assert.Empty(state.Pending);
        Assert.Empty(NewReleasePlanner.TakePending(state, T0));
    }

    [Fact]
    public void Only_albums_singles_and_eps_on_the_page_count()
    {
        var page = new ArtistPage
        {
            Artist = Artist("UCa", "A"),
            Sections =
            [
                new Shelf { Title = "Albums", Items = [Album("MPREb_1", "One", "2026"), Album("MPREb_1", "One again", "2026")] },
                new Shelf { Title = "Featured on", Items = [new Playlist { Title = "Mix", PlaylistId = "PL1" }] },
                new Shelf { Title = "Videos", Items = [new Track { Title = "Video", VideoId = "v1" }] },
                new Shelf { Title = "Fans might also like", Items = [Artist("UCz", "Other")] },
            ],
        };

        Assert.Equal(["MPREb_1"], NewReleasePlanner.Releases(page).Select(a => a.BrowseId));
    }

    internal static NewReleaseState Followed(DateTimeOffset refreshedAt, params (string Id, DateTimeOffset? CheckedAt)[] artists) => new()
    {
        FollowedRefreshedAt = refreshedAt,
        Artists = [.. artists.Select(a => new FollowedArtist { Id = a.Id, Name = a.Id, CheckedAt = a.CheckedAt, Known = a.CheckedAt is null ? null : [] })],
    };

    internal static Album Album(string id, string title, string? year, AlbumType type = AlbumType.Single) => new()
    {
        Title = title,
        BrowseId = id,
        Year = year,
        Type = type,
        Thumbnails = [new Thumbnail($"https://lh3.googleusercontent.com/{id}=w226-h226-l90-rj", 226, 226)],
    };

    internal static Artist Artist(string id, string name) => new() { Title = name, BrowseId = id };

    internal static ArtistPage Page(string name, params Album[] albums) => new()
    {
        Artist = Artist("UCx", name),
        Sections = albums.Length == 0 ? [] : [new Shelf { Title = "Albums", Items = albums }],
    };

    private static PendingRelease Pending(string id, DateTimeOffset foundAt) =>
        new("UCa", "A", id, id, AlbumType.Single, "2026", null, foundAt);
}

public sealed class NewReleaseServiceTests : IAsyncDisposable
{
    private readonly ReleaseFakeLibrary _library = new();
    private readonly ReleaseFakeBrowse _browse = new();
    private readonly ReleaseFakeAuth _auth = new();
    private readonly DiscordFakeSettings _settings = new();
    private readonly ReleaseTempPaths _paths = new();
    private readonly ManualTimeProvider _time = new();
    private readonly List<NewReleasesFoundEventArgs> _found = [];
    private NewReleaseService? _service;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
        }

        _paths.Dispose();
    }

    [Fact]
    public async Task Off_or_signed_out_nothing_runs()
    {
        _auth.Status = AuthStatus.SignedIn;
        var off = await StartAsync(on: false);
        Assert.Null(off.Run);

        await off.StopAsync(Ct);
        _auth.Status = AuthStatus.SignedOut;
        var signedOut = await StartAsync(on: true);
        Assert.Null(signedOut.Run);

        _time.Advance(TimeSpan.FromDays(2));
        await Task.Delay(50, Ct);
        Assert.Equal(0, _library.Calls);
        Assert.Empty(_browse.Calls);
    }

    [Fact]
    public async Task The_first_round_is_silent_and_a_later_release_is_announced_once()
    {
        _library.Artists = [NewReleasePlannerTests.Artist("UCa", "Daft Punk"), NewReleasePlannerTests.Artist("UCb", "Justice")];
        _browse.Pages["UCa"] = NewReleasePlannerTests.Page("Daft Punk", NewReleasePlannerTests.Album("MPREb_a1", "Homework", "2026"));
        _browse.Pages["UCb"] = NewReleasePlannerTests.Page("Justice", NewReleasePlannerTests.Album("MPREb_b1", "Cross", "2025"));
        _auth.Status = AuthStatus.SignedIn;
        await StartAsync(on: true);

        await DriveAsync(() => _browse.Calls.Count == 2);
        await Task.Delay(100, Ct);
        Assert.Empty(_found);
        Assert.Equal(1, _library.Calls);

        // Next day: a new single from Daft Punk.
        _browse.Pages["UCa"] = NewReleasePlannerTests.Page(
            "Daft Punk",
            NewReleasePlannerTests.Album("MPREb_a2", "One More Time (2026 Mix)", "2026"),
            NewReleasePlannerTests.Album("MPREb_a1", "Homework", "2026"));
        _time.Advance(TimeSpan.FromHours(24));
        await DriveAsync(() => _found.Count == 1);

        var release = Assert.Single(_found[0].Releases);
        Assert.Equal(("UCa", "Daft Punk", "MPREb_a2"), (release.ArtistId, release.ArtistName, release.Album.BrowseId));
        Assert.Equal([new ArtistRef("Daft Punk", "UCa")], release.Album.Artists);
        Assert.Equal(4, _browse.Calls.Count);
        Assert.Equal(2, _library.Calls);

        // The day after, nothing new: nothing more is announced.
        _time.Advance(TimeSpan.FromHours(24));
        await DriveAsync(() => _browse.Calls.Count == 6);
        await Task.Delay(100, Ct);
        Assert.Single(_found);
    }

    [Fact]
    public async Task Requests_are_spread_out()
    {
        _library.Artists = [NewReleasePlannerTests.Artist("UCa", "A"), NewReleasePlannerTests.Artist("UCb", "B"), NewReleasePlannerTests.Artist("UCc", "C")];
        _auth.Status = AuthStatus.SignedIn;
        await StartAsync(on: true, gap: TimeSpan.FromSeconds(20));

        await DriveAsync(() => _browse.Calls.Count == 3);

        var times = _browse.Calls.Select(c => c.At).Prepend(_library.LastCallAt).ToList();
        for (var i = 1; i < times.Count; i++)
        {
            Assert.True(times[i] - times[i - 1] >= TimeSpan.FromSeconds(20), $"request {i} came {times[i] - times[i - 1]} after the last");
        }
    }

    [Fact]
    public async Task A_restart_picks_up_where_the_last_run_stopped()
    {
        _library.Artists = [NewReleasePlannerTests.Artist("UCa", "A"), NewReleasePlannerTests.Artist("UCb", "B")];
        _auth.Status = AuthStatus.SignedIn;
        var first = await StartAsync(on: true);
        await DriveAsync(() => _browse.Calls.Count == 2);
        await first.StopAsync(Ct);
        Assert.True(File.Exists(first.FilePath));

        await StartAsync(on: true);
        _time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(200, Ct);

        Assert.Equal(1, _library.Calls);
        Assert.Equal(2, _browse.Calls.Count);
    }

    [Fact]
    public async Task Turning_it_off_stops_at_once_and_signing_out_forgets_everything()
    {
        _library.Artists = [NewReleasePlannerTests.Artist("UCa", "A"), NewReleasePlannerTests.Artist("UCb", "B")];
        _auth.Status = AuthStatus.SignedIn;
        var service = await StartAsync(on: true, gap: TimeSpan.FromMinutes(5));
        await DriveAsync(() => _browse.Calls.Count == 1, step: TimeSpan.FromSeconds(30));

        await _settings.UpdateAsync(s => s.NewReleaseAlerts = false, Ct);
        await WaitUntilAsync(() => service.Run is null);
        _time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(100, Ct);
        Assert.Single(_browse.Calls);
        Assert.True(File.Exists(service.FilePath));

        _auth.Raise(AuthStatus.SignedOut);
        await WaitUntilAsync(() => !File.Exists(service.FilePath));
    }

    [Fact]
    public async Task Signing_in_again_starts_a_new_round()
    {
        _library.Artists = [NewReleasePlannerTests.Artist("UCa", "A")];
        _auth.Status = AuthStatus.SignedOut;
        var service = await StartAsync(on: true);
        Assert.Null(service.Run);

        _auth.Raise(AuthStatus.SignedIn);
        await WaitUntilAsync(() => service.Run is not null);
        await DriveAsync(() => _browse.Calls.Count == 1);
    }

    [Fact]
    public async Task One_failing_artist_is_skipped_until_tomorrow()
    {
        _library.Artists = [NewReleasePlannerTests.Artist("UCbad", "Bad"), NewReleasePlannerTests.Artist("UCok", "Ok")];
        _browse.Failing.Add("UCbad");
        _auth.Status = AuthStatus.SignedIn;
        await StartAsync(on: true);

        await DriveAsync(() => _browse.Calls.Count == 2);
        _time.Advance(TimeSpan.FromHours(1));
        await Task.Delay(100, Ct);

        Assert.Equal(["UCbad", "UCok"], _browse.Calls.Select(c => c.Id).Order());
    }

    private async Task<NewReleaseService> StartAsync(bool on, TimeSpan? gap = null)
    {
        _library.Clock = _time;
        _browse.Clock = _time;
        _settings.Current.NewReleaseAlerts = on;
        _service = new NewReleaseService(_library, _browse, _auth, _settings, _paths, _time, NullLogger<NewReleaseService>.Instance)
        {
            FirstDelay = TimeSpan.FromSeconds(1),
            MinGap = gap ?? TimeSpan.FromSeconds(1),
            MaxGap = gap ?? TimeSpan.FromSeconds(1),
        };
        _service.Found += (_, e) =>
        {
            lock (_found)
            {
                _found.Add(e);
            }
        };
        await _service.StartAsync(Ct);
        return _service;
    }

    // Moves the clock a step at a time until the round got as far as the test needs.
    private async Task DriveAsync(Func<bool> done, TimeSpan? step = null)
    {
        for (var i = 0; i < 2000 && !done(); i++)
        {
            _time.Advance(step ?? TimeSpan.FromSeconds(1));
            await Task.Delay(5, Ct);
        }

        Assert.True(done(), "The round didn't get there.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }
}

public sealed class NewReleaseNotificationContentTests
{
    [Fact]
    public void One_release_names_it_and_opens_its_album()
    {
        NewRelease[] releases = [Release("Daft Punk", "MPREb_1", "Homework", AlbumType.Single)];

        Assert.Equal(("Homework", "New single from Daft Punk"), NewReleaseNotificationContent.Text(releases));
        var toast = XElement.Parse(NewReleaseNotificationContent.Build(releases, null));
        Assert.Equal("action=new-release&album=MPREb_1", (string?)toast.Attribute("launch"));
        Assert.Equal("true", (string?)toast.Element("audio")?.Attribute("silent"));
        Assert.Null(toast.Descendants("image").FirstOrDefault());
    }

    [Theory]
    [InlineData(2, "From A and B")]
    [InlineData(3, "From A, B and C")]
    [InlineData(5, "From A, B and 3 more")]
    public void Several_releases_are_counted_and_open_the_list(int count, string from)
    {
        var releases = Enumerable.Range(0, count).Select(i => Release(((char)('A' + i)).ToString(), "MPREb_" + i, "T" + i, AlbumType.Album)).ToList();

        Assert.Equal(($"{count} new releases from artists you follow", from), NewReleaseNotificationContent.Text(releases));
        Assert.Equal(NewReleaseNotificationContent.ListArgument, (string?)XElement.Parse(NewReleaseNotificationContent.Build(releases, null)).Attribute("launch"));
    }

    [Fact]
    public void The_same_artist_is_named_once()
    {
        NewRelease[] releases = [Release("Daft Punk", "MPREb_1", "One", AlbumType.EP), Release("Daft Punk", "MPREb_2", "Two", AlbumType.Album)];

        Assert.Equal("From Daft Punk", NewReleaseNotificationContent.Text(releases).Message);
    }

    [Fact]
    public void The_cover_is_the_app_logo()
    {
        var path = Path.Combine(Path.GetTempPath(), "cover.png");
        var toast = XElement.Parse(NewReleaseNotificationContent.Build([Release("A", "MPREb_1", "T", AlbumType.EP)], path));

        var image = Assert.Single(toast.Descendants("image"));
        Assert.Equal("appLogoOverride", (string?)image.Attribute("placement"));
        Assert.Equal(new Uri(path).AbsoluteUri, (string?)image.Attribute("src"));
    }

    [Theory]
    [InlineData("action=new-releases", true, null)]
    [InlineData("action=new-release&album=MPREb_x-1", true, "MPREb_x-1")]
    [InlineData("action=new-release&album=", false, null)]
    [InlineData("action=new-release&album=bad id", false, null)]
    [InlineData("action=next", false, null)]
    [InlineData(null, false, null)]
    public void Clicks_are_read_back(string? arguments, bool handled, string? albumId)
    {
        Assert.Equal(handled, NewReleaseNotificationContent.TryParse(arguments, out var parsed));
        Assert.Equal(albumId, parsed);
    }

    private static NewRelease Release(string artist, string id, string title, AlbumType type) =>
        new("UC" + artist, artist, new Album { Title = title, BrowseId = id, Type = type });
}

internal sealed class ReleaseFakeLibrary : ILibraryApi
{
    public IReadOnlyList<Artist> Artists { get; set; } = [];

    public int Calls { get; private set; }

    public DateTimeOffset LastCallAt { get; private set; }

    public TimeProvider? Clock { get; set; }

    public Task<Paged<Artist>> GetSubscriptionsAsync(string? continuation = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastCallAt = Clock?.GetUtcNow() ?? default;
        return Task.FromResult(new Paged<Artist>(Artists, null));
    }

    public Task<Paged<Playlist>> GetPlaylistsAsync(string? continuation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Paged<Track>> GetSongsAsync(string? continuation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Paged<Album>> GetAlbumsAsync(string? continuation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Paged<Artist>> GetArtistsAsync(string? continuation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Paged<Track>> GetLikedSongsAsync(string? continuation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<Shelf>> GetHistoryAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class ReleaseFakeBrowse : IBrowseApi
{
    private readonly List<(string Id, DateTimeOffset At)> _calls = [];

    public Dictionary<string, ArtistPage> Pages { get; } = [];

    public HashSet<string> Failing { get; } = [];

    public TimeProvider? Clock { get; set; }

    public IReadOnlyList<(string Id, DateTimeOffset At)> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public Task<ArtistPage> GetArtistAsync(string channelId, CancellationToken cancellationToken = default)
    {
        lock (_calls)
        {
            _calls.Add((channelId, Clock?.GetUtcNow() ?? default));
        }

        if (Failing.Contains(channelId))
        {
            return Task.FromException<ArtistPage>(new InnerTubeException("browse", "HTTP 404"));
        }

        return Task.FromResult(Pages.TryGetValue(channelId, out var page)
            ? page
            : NewReleasePlannerTests.Page(channelId, NewReleasePlannerTests.Album("MPREb_" + channelId, "Debut", "2026")));
    }

    public Task<Paged<Shelf>> GetHomeAsync(string? continuation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<AlbumPage> GetAlbumAsync(string browseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Paged<Album>> GetArtistAlbumsAsync(string browseId, string? browseParams, string? continuation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<PlaylistPage> GetPlaylistAsync(string playlistId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Paged<Track>> GetPlaylistTracksAsync(string continuation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class ReleaseFakeAuth : IAuthService
{
    public AuthStatus Status { get; set; }

    public AuthMode Mode => Status == AuthStatus.SignedIn ? AuthMode.Cookies : AuthMode.None;

    public event EventHandler<AuthStatusChangedEventArgs>? StatusChanged;

    public void Raise(AuthStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, new AuthStatusChangedEventArgs(status, Mode));
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SignInWithCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SignInWithCookieHeaderAsync(string cookieHeader, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class ReleaseTempPaths : IAppPaths, IDisposable
{
    public string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "hush-releases-" + Guid.NewGuid().ToString("N"))).FullName;

    public string Logs => Sub("logs");

    public string Cache => Sub("cache");

    public string ImageCache => Sub("cache/images");

    public string Secure => Sub("secure");

    public string Tools => Sub("tools");

    public string SettingsFile => Path.Combine(Root, "settings.json");

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

    private string Sub(string name) => Directory.CreateDirectory(Path.Combine(Root, name)).FullName;
}
