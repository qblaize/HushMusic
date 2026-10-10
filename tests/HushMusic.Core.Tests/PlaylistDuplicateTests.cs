using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

/// <summary>Adding songs that are already in the playlist: refused, added anyway, or only the missing ones.</summary>
public sealed class PlaylistDuplicateTests
{
    private readonly FakePlaylistServer _server = new();
    private readonly AccountActionsService _service;
    private readonly List<PlaylistChangedEventArgs> _changes = [];

    public PlaylistDuplicateTests()
    {
        _service = new AccountActionsService(_server, _server);
        _service.PlaylistChanged += (_, e) => _changes.Add(e);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task New_songs_are_added_with_the_server_check()
    {
        _server.Entries.Add("a");

        await _service.AddToPlaylistAsync("PL1", ["b", "c"], cancellationToken: Ct);

        Assert.Equal(["add b,c"], _server.Calls);
        Assert.Equal(["a", "b", "c"], _server.Entries);
        var change = Assert.Single(_changes);
        Assert.Equal(PlaylistChangeKind.ItemsAdded, change.Kind);
        Assert.Equal(["b", "c"], change.VideoIds);
    }

    [Fact]
    public async Task Refused_add_changes_nothing_and_raises_no_event()
    {
        _server.Entries.Add("a");

        var ex = await Assert.ThrowsAsync<AlreadyInPlaylistException>(() => _service.AddToPlaylistAsync("PL1", ["a", "b"], cancellationToken: Ct));

        Assert.Equal(["a", "b"], ex.VideoIds);
        Assert.Equal(["a"], _server.Entries);
        Assert.Empty(_changes);
    }

    [Fact]
    public async Task Adding_anyway_passes_duplicates_through()
    {
        _server.Entries.Add("a");

        await _service.AddToPlaylistAsync("PL1", ["a", "b"], allowDuplicates: true, Ct);

        Assert.Equal(["add a,b (duplicates)"], _server.Calls);
        Assert.Equal(["a", "a", "b"], _server.Entries);
        Assert.Equal(["a", "b"], Assert.Single(_changes).VideoIds);
    }

    [Fact]
    public async Task Skipping_duplicates_reads_every_page_and_adds_only_the_missing_songs()
    {
        _server.PageSize = 2;
        _server.Entries.AddRange(["a", "b", "c", "d", "e"]);

        var added = await _service.AddMissingToPlaylistAsync("VLPL1", ["x", "e", "a", "y", "x"], Ct);

        Assert.Equal(["x", "y"], added);
        Assert.Equal(["browse VLPL1", "continue 1", "continue 2", "add x,y"], _server.Calls);
        Assert.Equal(["x", "y"], Assert.Single(_changes).VideoIds);
    }

    [Fact]
    public async Task Skipping_when_every_song_is_there_sends_nothing()
    {
        _server.Entries.AddRange(["a", "b"]);

        var added = await _service.AddMissingToPlaylistAsync("PL1", ["b", "a"], Ct);

        Assert.Empty(added);
        Assert.Equal(["browse PL1"], _server.Calls);
        Assert.Empty(_changes);
    }

    [Fact]
    public async Task Skipping_keeps_the_server_check_for_songs_added_meanwhile()
    {
        _server.Entries.Add("a");
        _server.AfterBrowse = () => _server.Entries.Add("b");

        await Assert.ThrowsAsync<AlreadyInPlaylistException>(() => _service.AddMissingToPlaylistAsync("PL1", ["a", "b"], Ct));

        Assert.Equal(["a", "b"], _server.Entries);
        Assert.Empty(_changes);
    }

    [Fact]
    public async Task Skipping_stops_reading_a_playlist_that_never_ends()
    {
        _server.PageSize = 1;
        _server.Endless = true;
        _server.Entries.Add("a");

        var added = await _service.AddMissingToPlaylistAsync("PL1", ["b"], Ct);

        Assert.Equal(["b"], added);
        Assert.InRange(_server.Calls.Count(c => c.StartsWith("continue", StringComparison.Ordinal)), 10, 1000);
    }

    /// <summary>
    /// One playlist, served through both APIs. Like YouTube Music, an add without duplicates allowed is refused as a whole
    /// when any song is already there. Pages hold <see cref="PageSize"/> songs.
    /// </summary>
    private sealed class FakePlaylistServer : IAccountApi, IBrowseApi
    {
        public List<string> Entries { get; } = [];

        public List<string> Calls { get; } = [];

        public int PageSize { get; set; } = 100;

        /// <summary>Every page has a continuation, even past the last song.</summary>
        public bool Endless { get; set; }

        public Action? AfterBrowse { get; set; }

        public Task<IReadOnlyList<PlaylistEntryRef>> AddPlaylistItemsAsync(string playlistId, IReadOnlyList<string> videoIds, bool allowDuplicates = false, CancellationToken cancellationToken = default)
        {
            Calls.Add($"add {string.Join(',', videoIds)}{(allowDuplicates ? " (duplicates)" : string.Empty)}");
            if (!allowDuplicates && videoIds.Any(Entries.Contains))
            {
                throw new AlreadyInPlaylistException(playlistId, videoIds);
            }

            Entries.AddRange(videoIds);
            IReadOnlyList<PlaylistEntryRef> added = [.. videoIds.Select((v, i) => new PlaylistEntryRef(v, "N" + i))];
            return Task.FromResult(added);
        }

        public Task<PlaylistPage> GetPlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
        {
            Calls.Add($"browse {playlistId}");
            var page = new PlaylistPage { Playlist = new Playlist { Title = "Mine", PlaylistId = playlistId }, IsOwned = true, Tracks = Page(0) };
            AfterBrowse?.Invoke();
            return Task.FromResult(page);
        }

        public Task<Paged<Track>> GetPlaylistTracksAsync(string continuation, CancellationToken cancellationToken = default)
        {
            Calls.Add($"continue {continuation}");
            return Task.FromResult(Page(int.Parse(continuation, System.Globalization.CultureInfo.InvariantCulture)));
        }

        private Paged<Track> Page(int index)
        {
            var tracks = TestData.Tracks([.. Entries.Skip(index * PageSize).Take(PageSize)]);
            var more = Endless || (index + 1) * PageSize < Entries.Count;
            return new Paged<Track>(tracks, more ? (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        }

        public Task<Paged<Shelf>> GetHomeAsync(string? continuation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AlbumPage> GetAlbumAsync(string browseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ArtistPage> GetArtistAsync(string channelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Paged<Album>> GetArtistAlbumsAsync(string browseId, string? browseParams, string? continuation = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AccountInfo?> GetAccountInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RateSongAsync(string videoId, LikeStatus status, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> CreatePlaylistAsync(string title, string? description, PrivacyStatus privacy, IReadOnlyList<string>? videoIds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RemovePlaylistItemsAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task MovePlaylistItemAsync(string playlistId, string setVideoId, string? successorSetVideoId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task EditPlaylistAsync(string playlistId, string? title = null, string? description = null, PrivacyStatus? privacy = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task AddHistoryItemAsync(string videoId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
