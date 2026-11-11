using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Models;
using HushMusic.Core.Queue;
using HushMusic.Core.Services;
using Xunit;
using static HushMusic.Core.Tests.TestData;

namespace HushMusic.Core.Tests;

/// <summary>Disliked songs and blocked artists: the matching, the store, the one filter and what it does to the queue.</summary>
public sealed class BlocklistTests : IDisposable
{
    private static readonly DateTimeOffset BlockedAt = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hushmusic-tests", Guid.NewGuid().ToString("N"));

    public BlocklistTests() => Directory.CreateDirectory(_root);

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

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Track By(string videoId, params ArtistRef[] artists) => Track(videoId) with { Artists = artists };

    private static BlockedArtist Blocked(string name, string? id = null) => new(name, id, BlockedAt);

    // ===== Matching =====

    [Fact]
    public void An_artist_with_an_id_matches_by_id_and_one_without_by_name()
    {
        var matcher = new ArtistMatcher([Blocked("Drake", "UC1")]);

        Assert.True(matcher.IsBlocked(new ArtistRef("Drake", "UC1")));
        Assert.True(matcher.IsBlocked(new ArtistRef("Someone else", "UC1")));
        Assert.True(matcher.IsBlocked(new ArtistRef("drake", null))); // a radio match without an id
        Assert.False(matcher.IsBlocked(new ArtistRef("Drake", "UC2"))); // another artist with the same name
    }

    [Fact]
    public void An_artist_blocked_without_an_id_matches_every_artist_with_that_name()
    {
        var matcher = new ArtistMatcher([Blocked(" Beyoncé ")]);

        Assert.True(matcher.IsBlocked(new ArtistRef("BEYONCE", "UC9")));
        Assert.True(matcher.IsBlocked(new ArtistRef("beyonce", null)));
        Assert.False(matcher.IsBlocked(new ArtistRef("Beyoncé Fan Club", null)));
    }

    [Fact]
    public void A_song_is_blocked_when_any_of_its_artists_is()
    {
        var matcher = new ArtistMatcher([Blocked("Dua Lipa", "UC2")]);

        Assert.True(matcher.IsBlocked(By("a", new ArtistRef("Calvin Harris", "UC1"), new ArtistRef("Dua Lipa", "UC2"))));
        Assert.False(matcher.IsBlocked(By("b", new ArtistRef("Calvin Harris", "UC1"))));
        Assert.False(matcher.IsBlocked(Track("c")));
        Assert.False(ArtistMatcher.Empty.IsBlocked(By("d", new ArtistRef("Dua Lipa", "UC2"))));
    }

    [Fact]
    public void An_entry_stands_for_the_same_id_or_when_an_id_is_missing_the_same_name()
    {
        Assert.True(ArtistMatcher.Matches(Blocked("A", "UC1"), new ArtistRef("Renamed", "UC1")));
        Assert.False(ArtistMatcher.Matches(Blocked("A", "UC1"), new ArtistRef("A", "UC2")));
        Assert.True(ArtistMatcher.Matches(Blocked("A", "UC1"), new ArtistRef("a", null)));
        Assert.True(ArtistMatcher.Matches(Blocked("A"), new ArtistRef("a", "UC2")));
        Assert.False(ArtistMatcher.Matches(Blocked("A"), new ArtistRef("B", null)));
    }

    // ===== The store =====

    [Fact]
    public async Task Ratings_made_in_hush_keep_the_disliked_songs_and_they_are_saved()
    {
        var api = new FakeAccountApi();
        var account = new AccountActionsService(api, new FakeBrowseApi());
        var service = await StartAsync(account);
        var changes = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref changes);

        await account.RateTrackAsync("a", LikeStatus.Dislike, Ct);
        await account.RateTrackAsync("b", LikeStatus.Dislike, Ct);
        await account.RateTrackAsync("b", LikeStatus.Like, Ct);

        Assert.True(service.IsDisliked("a"));
        Assert.False(service.IsDisliked("b"));
        Assert.True(service.IsLeftOut(Track("a")));
        Assert.Equal(3, changes);
        await service.StopAsync(CancellationToken.None);

        var reloaded = await StartAsync(account);
        Assert.True(reloaded.IsDisliked("a"));
        Assert.False(reloaded.IsDisliked("b"));

        await account.RateTrackAsync("a", LikeStatus.Indifferent, Ct);
        Assert.False(reloaded.IsDisliked("a"));
        await reloaded.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Disliked_songs_go_on_sign_out_and_account_switch_and_blocked_artists_stay()
    {
        var account = new AccountActionsService(new FakeAccountApi(), new FakeBrowseApi());
        var auth = new StubAuth(AuthStatus.SignedOut);
        var service = await StartAsync(account, auth);
        await service.BlockAsync(new ArtistRef("Blocked", "UC1"), Ct);

        // Restoring the stored session at startup keeps them.
        await account.RateTrackAsync("a", LikeStatus.Dislike, Ct);
        auth.Raise(AuthStatus.SignedIn);
        Assert.True(service.IsDisliked("a"));

        // Signing in again over the session: maybe another account.
        auth.Raise(AuthStatus.SignedIn);
        Assert.False(service.IsDisliked("a"));

        await account.RateTrackAsync("b", LikeStatus.Dislike, Ct);
        auth.Raise(AuthStatus.Expired);
        Assert.True(service.IsDisliked("b"));
        auth.Raise(AuthStatus.SignedIn);
        Assert.False(service.IsDisliked("b"));

        await account.RateTrackAsync("c", LikeStatus.Dislike, Ct);
        auth.Raise(AuthStatus.SignedOut);
        Assert.False(service.IsDisliked("c"));
        Assert.True(service.IsBlocked(new ArtistRef("Blocked", "UC1")));
        await service.StopAsync(CancellationToken.None);

        var reloaded = await StartAsync(account);
        Assert.False(reloaded.IsDisliked("c"));
        Assert.Single(reloaded.BlockedArtists);
        await reloaded.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_song_parsed_as_disliked_is_left_out_too()
    {
        var service = await StartAsync();

        Assert.True(service.IsLeftOut(Track("a") with { LikeStatus = LikeStatus.Dislike }));
        Assert.False(service.IsLeftOut(Track("a") with { LikeStatus = LikeStatus.Indifferent }));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Blocking_and_unblocking_an_artist_is_kept()
    {
        var service = await StartAsync();

        Assert.True(await service.BlockAsync(new ArtistRef("Drake", "UC1"), Ct));
        Assert.False(await service.BlockAsync(new ArtistRef("drake", null), Ct)); // the same artist
        Assert.True(await service.BlockAsync(new ArtistRef("Radio Artist", null), Ct));

        Assert.Equal(["Radio Artist", "Drake"], service.BlockedArtists.Select(a => a.Name));
        Assert.True(service.IsLeftOut(By("x", new ArtistRef("Drake", "UC1"))));
        Assert.True(service.IsLeftOut(By("y", new ArtistRef("radio artist", "UC7"))));
        await service.StopAsync(CancellationToken.None);

        var reloaded = await StartAsync();
        var drake = reloaded.BlockedArtists.Single(a => a.BrowseId == "UC1");
        Assert.Equal("Drake", drake.Name);

        Assert.True(await reloaded.UnblockAsync(drake.ToArtistRef(), Ct));
        Assert.False(await reloaded.UnblockAsync(drake.ToArtistRef(), Ct));
        Assert.False(reloaded.IsBlocked(new ArtistRef("Drake", "UC1")));
        Assert.Equal(["Radio Artist"], reloaded.BlockedArtists.Select(a => a.Name));
        await reloaded.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task An_unreadable_file_starts_an_empty_list()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, BlocklistService.FileName), "{ not json", Ct);
        var service = await StartAsync();

        Assert.Empty(service.BlockedArtists);
        Assert.True(await service.BlockAsync(new ArtistRef("A", "UC1"), Ct));
        await service.StopAsync(CancellationToken.None);
    }

    // ===== One filter =====

    [Fact]
    public void The_filter_combines_smart_skip_and_the_blocklist()
    {
        var blocklist = new StubBlocklist(disliked: ["disliked"], artists: [Blocked("Blocked", "UC1")]);
        var filter = new AutoQueueFilter(new StubSmartSkip("skipped"), blocklist);

        var tracks = new[] { Track("skipped"), Track("disliked"), By("by", new ArtistRef("Blocked", "UC1")), Track("fine") };
        Assert.Equal(["fine"], filter.WithoutLeftOut(tracks).Select(t => t.VideoId));
        Assert.False(new AutoQueueFilter().IsLeftOut(Track("skipped")));

        var changes = 0;
        EventHandler handler = (_, _) => changes++;
        filter.Changed += handler;
        blocklist.RaiseChanged();
        filter.Changed -= handler;
        blocklist.RaiseChanged();
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task Radio_leaves_out_disliked_songs_and_blocked_artists_but_plays_the_picked_song()
    {
        var queue = new QueueService();
        var blocked = new ArtistRef("Blocked", "UC1");
        var watch = new FakeWatchApi
        {
            OnGet = call => Task.FromResult(new WatchPlaylist { Tracks = [By(call.VideoId!, blocked), Track("x"), Track("disliked"), By("b", blocked), Track("y")] }),
        };
        var filter = new AutoQueueFilter(blocklist: new StubBlocklist(disliked: ["disliked"], artists: [Blocked("Blocked", "UC1")]));
        var actions = new PlaybackActions(queue, new FakePlayer(), watch, new FakeBrowseApi(), new FakeNotifications(), NullLogger<PlaybackActions>.Instance, filter: filter);

        await actions.StartRadioAsync(By("seed", blocked), Ct);

        Assert.Equal(["seed", "x", "y"], Ids(queue.Items));
    }

    [Fact]
    public void Blocked_artists_stay_out_of_the_weekly_mix()
    {
        var filter = new AutoQueueFilter(blocklist: new StubBlocklist(disliked: ["d"], artists: [Blocked("Blocked")]));
        var source = new[] { By("a", new ArtistRef("Blocked", null)), Track("d"), By("c", new ArtistRef("Other", "UC2")) };

        var mix = WeeklyMixPlanner.Merge([source], HeardSongs.From([]), filter.IsLeftOut, new Random(1));

        Assert.Equal(["c"], mix.Select(t => t.VideoId));
    }

    // ===== What a change does to the queue =====

    [Fact]
    public async Task A_new_dislike_or_block_takes_the_left_out_songs_out_of_a_radio_queue()
    {
        var queue = new QueueService();
        var blocklist = new StubBlocklist();
        var extender = new QueueAutoExtender(queue, new FakeWatchApi(), NullLogger<QueueAutoExtender>.Instance, new AutoQueueFilter(blocklist: blocklist));
        await extender.StartAsync(Ct);
        try
        {
            var blocked = new ArtistRef("Blocked", "UC1");
            queue.Load([By("seed", blocked), Track("a"), By("b", blocked), Track("c"), Track("d"), Track("e")], 0, new QueueSource(QueueSourceKind.Radio, "seed"));

            blocklist.Disliked.Add("c");
            blocklist.Artists.Add(Blocked("Blocked", "UC1"));
            blocklist.RaiseChanged();

            // The song playing stays, even by a blocked artist.
            Assert.Equal(["seed", "a", "d", "e"], Ids(queue.Items));
            Assert.Equal(0, queue.CurrentIndex);
        }
        finally
        {
            await extender.StopAsync(CancellationToken.None);
            extender.Dispose();
        }
    }

    [Fact]
    public async Task Playlists_the_user_plays_keep_their_songs_when_the_filter_changes()
    {
        var queue = new QueueService();
        var blocklist = new StubBlocklist();
        var extender = new QueueAutoExtender(queue, new FakeWatchApi(), NullLogger<QueueAutoExtender>.Instance, new AutoQueueFilter(blocklist: blocklist));
        await extender.StartAsync(Ct);
        try
        {
            queue.Load(Tracks("a", "b", "c", "d", "e"), 0, new QueueSource(QueueSourceKind.Playlist, "PL1"));

            blocklist.Disliked.Add("c");
            blocklist.RaiseChanged();

            Assert.Equal(["a", "b", "c", "d", "e"], Ids(queue.Items));
        }
        finally
        {
            await extender.StopAsync(CancellationToken.None);
            extender.Dispose();
        }
    }

    [Fact]
    public async Task Autoplay_drops_its_suggestions_that_are_left_out_now_and_keeps_the_album()
    {
        var queue = new QueueService();
        var player = new FakePlayer();
        var blocklist = new StubBlocklist();
        var watch = new FakeWatchApi { OnGet = call => Task.FromResult(new WatchPlaylist { Tracks = Tracks(call.VideoId!, "s1", "s2", "s3") }) };
        var autoplay = new QueueAutoplay(queue, player, watch, new FakeSettings(), NullLogger<QueueAutoplay>.Instance, new AutoQueueFilter(blocklist: blocklist));
        await autoplay.StartAsync(Ct);
        try
        {
            queue.Load(Tracks("x", "a"), 1, new QueueSource(QueueSourceKind.Album, "MPREb_1"));
            player.RaiseTrackStarted(queue.Items[1]);
            await WaitUntilAsync(() => autoplay.SuggestedItemIds.Count == 3);
            Assert.Equal(["x", "a", "s1", "s2", "s3"], Ids(queue.Items));

            blocklist.Disliked.UnionWith(["x", "s2"]);
            blocklist.RaiseChanged();

            Assert.Equal(["x", "a", "s1", "s3"], Ids(queue.Items));
            Assert.Equal(2, autoplay.SuggestedItemIds.Count);
        }
        finally
        {
            await autoplay.StopAsync(CancellationToken.None);
            autoplay.Dispose();
        }
    }

    [Fact]
    public async Task Disliking_the_song_that_is_playing_skips_it()
    {
        var player = new FakePlayer { CurrentTrack = Track("playing") };
        var account = new AccountActionsService(new FakeAccountApi(), new FakeBrowseApi());
        var skipper = new DislikedSongSkipper(account, player, NullLogger<DislikedSongSkipper>.Instance);
        await skipper.StartAsync(Ct);

        await account.RateTrackAsync("other", LikeStatus.Dislike, Ct);
        await account.RateTrackAsync("playing", LikeStatus.Like, Ct);
        Assert.Equal(0, player.NextCount);

        await account.RateTrackAsync("playing", LikeStatus.Dislike, Ct);
        Assert.Equal(1, player.NextCount);

        await skipper.StopAsync(CancellationToken.None);
        await account.RateTrackAsync("playing", LikeStatus.Dislike, Ct);
        Assert.Equal(1, player.NextCount);
    }

    private async Task<BlocklistService> StartAsync(IAccountActionsService? account = null, IAuthService? auth = null)
    {
        var service = new BlocklistService(
            account ?? new AccountActionsService(new FakeAccountApi(), new FakeBrowseApi()),
            auth ?? new StubAuth(AuthStatus.SignedIn),
            new AppPaths(_root),
            TimeProvider.System,
            NullLogger<BlocklistService>.Instance);
        await service.StartAsync(Ct);
        await service.LoadAsync(Ct);
        return service;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private sealed class StubBlocklist(IEnumerable<string>? disliked = null, IEnumerable<BlockedArtist>? artists = null) : IBlocklist
    {
        public HashSet<string> Disliked { get; } = [.. disliked ?? []];

        public List<BlockedArtist> Artists { get; } = [.. artists ?? []];

        public IReadOnlyList<BlockedArtist> BlockedArtists => Artists;

        public event EventHandler? Changed;

        public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

        public bool IsDisliked(string videoId) => Disliked.Contains(videoId);

        public bool IsBlocked(ArtistRef artist) => new ArtistMatcher(Artists).IsBlocked(artist);

        public bool IsLeftOut(Track track) => Disliked.Contains(track.VideoId) || new ArtistMatcher(Artists).IsBlocked(track);

        public Task<bool> BlockAsync(ArtistRef artist, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> UnblockAsync(ArtistRef artist, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubAuth(AuthStatus status) : IAuthService
    {
        public AuthStatus Status { get; private set; } = status;

        public AuthMode Mode => Status is AuthStatus.SignedIn or AuthStatus.Expired ? AuthMode.Cookies : AuthMode.None;

        public event EventHandler<AuthStatusChangedEventArgs>? StatusChanged;

        public void Raise(AuthStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, new AuthStatusChangedEventArgs(status, Mode));
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SignInWithCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SignInWithCookieHeaderAsync(string cookieHeader, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SignOutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StubSmartSkip(params string[] leftOut) : ISmartSkip
    {
        public IReadOnlyList<SkippedSong> OftenSkipped => [];

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool IsLeftOut(string videoId) => leftOut.Contains(videoId);

        public Task ForgiveAsync(string videoId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
