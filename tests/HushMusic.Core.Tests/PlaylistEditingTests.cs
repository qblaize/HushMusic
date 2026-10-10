using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

/// <summary>Reordering playlist entries and undoing a removal (AccountActionsService over IAccountApi).</summary>
public sealed class PlaylistEditingTests
{
    private readonly RecordingAccountApi _api = new();
    private readonly AccountActionsService _service;
    private readonly List<PlaylistChangedEventArgs> _changes = [];

    public PlaylistEditingTests()
    {
        _service = new AccountActionsService(_api, new FakeBrowseApi());
        _service.PlaylistChanged += (_, e) => _changes.Add(e);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Move_sends_the_entry_and_its_new_successor()
    {
        await _service.MovePlaylistItemAsync("PL1", Entry("b", "S_B"), Entry("a", "S_A"), Ct);

        Assert.Equal(["move S_B before S_A"], _api.Calls);
        Assert.Equal(PlaylistChangeKind.ItemsMoved, Assert.Single(_changes).Kind);
    }

    [Fact]
    public async Task Move_to_the_end_has_no_successor()
    {
        await _service.MovePlaylistItemAsync("PL1", Entry("b", "S_B"), null, Ct);

        Assert.Equal(["move S_B to end"], _api.Calls);
    }

    [Fact]
    public async Task Move_needs_entry_ids()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.MovePlaylistItemAsync("PL1", Entry("b", null), null, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => _service.MovePlaylistItemAsync("PL1", Entry("b", "S_B"), Entry("a", null), Ct));

        Assert.Empty(_api.Calls);
        Assert.Empty(_changes);
    }

    [Fact]
    public async Task Failed_move_raises_no_event()
    {
        _api.Fail = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.MovePlaylistItemAsync("PL1", Entry("b", "S_B"), null, Ct));

        Assert.Empty(_changes);
    }

    [Fact]
    public async Task Restore_puts_entries_back_before_their_old_successors()
    {
        // Playlist A B C D E; B and D were removed.
        RemovedPlaylistEntry[] removed = [new(Entry("b", "S_B"), "S_C"), new(Entry("d", "S_D"), "S_E")];

        var restored = await _service.RestorePlaylistItemsAsync("PL1", removed, Ct);

        Assert.Equal(["add b,d (duplicates)", "move N1 before S_E", "move N0 before S_C"], _api.Calls);
        Assert.Equal(["N0", "N1"], restored.Select(t => t.SetVideoId));
        Assert.Equal(["b", "d"], restored.Select(t => t.VideoId));
        Assert.Equal(PlaylistChangeKind.ItemsAdded, Assert.Single(_changes).Kind);
    }

    [Fact]
    public async Task Restore_of_neighbours_uses_the_new_id_of_the_restored_successor()
    {
        // Playlist A B C D; B and C were removed (B was followed by C).
        RemovedPlaylistEntry[] removed = [new(Entry("b", "S_B"), "S_C"), new(Entry("c", "S_C"), "S_D")];

        await _service.RestorePlaylistItemsAsync("PL1", removed, Ct);

        Assert.Equal(["add b,c (duplicates)", "move N1 before S_D", "move N0 before N1"], _api.Calls);
    }

    [Fact]
    public async Task Restore_of_the_last_entries_needs_no_moves()
    {
        // Playlist A B C; B and C (the end) were removed: adding them back puts them in place.
        RemovedPlaylistEntry[] removed = [new(Entry("b", "S_B"), "S_C"), new(Entry("c", "S_C"), null)];

        await _service.RestorePlaylistItemsAsync("PL1", removed, Ct);

        Assert.Equal(["add b,c (duplicates)"], _api.Calls);
    }

    [Fact]
    public async Task Restore_matches_duplicate_songs_in_order()
    {
        // The same song twice: A X B X C, both X removed.
        RemovedPlaylistEntry[] removed = [new(Entry("x", "S_X1"), "S_B"), new(Entry("x", "S_X2"), "S_C")];

        var restored = await _service.RestorePlaylistItemsAsync("PL1", removed, Ct);

        Assert.Equal(["N0", "N1"], restored.Select(t => t.SetVideoId));
        Assert.Equal(["add x,x (duplicates)", "move N1 before S_C", "move N0 before S_B"], _api.Calls);
    }

    [Fact]
    public async Task Restore_without_reported_ids_leaves_entries_at_the_end()
    {
        _api.ReportIds = false;
        RemovedPlaylistEntry[] removed = [new(Entry("b", "S_B"), "S_C")];

        var restored = await _service.RestorePlaylistItemsAsync("PL1", removed, Ct);

        Assert.Equal(["add b (duplicates)"], _api.Calls);
        Assert.Null(Assert.Single(restored).SetVideoId);
    }

    [Fact]
    public async Task Restore_skips_entries_whose_successor_did_not_come_back()
    {
        // Only the first entry got an id back; it was followed by the second, which has no place yet to go before.
        _api.ReportOnly = "b";
        RemovedPlaylistEntry[] removed = [new(Entry("b", "S_B"), "S_C"), new(Entry("c", "S_C"), "S_D")];

        var restored = await _service.RestorePlaylistItemsAsync("PL1", removed, Ct);

        Assert.Equal(["add b,c (duplicates)"], _api.Calls);
        Assert.Equal(["N0", null], restored.Select(t => t.SetVideoId));
    }

    [Fact]
    public async Task Restore_of_nothing_sends_nothing()
    {
        Assert.Empty(await _service.RestorePlaylistItemsAsync("PL1", [], Ct));
        Assert.Empty(_api.Calls);
        Assert.Empty(_changes);
    }

    private static Track Entry(string videoId, string? setVideoId) => TestData.Track(videoId) with { SetVideoId = setVideoId };

    /// <summary>Records playlist edits as short strings; added entries get the ids N0, N1, ...</summary>
    private sealed class RecordingAccountApi : IAccountApi
    {
        public List<string> Calls { get; } = [];

        public bool Fail { get; set; }

        public bool ReportIds { get; set; } = true;

        /// <summary>Only report a new id for this video (the rest are missing from the response).</summary>
        public string? ReportOnly { get; set; }

        public Task<IReadOnlyList<PlaylistEntryRef>> AddPlaylistItemsAsync(string playlistId, IReadOnlyList<string> videoIds, bool allowDuplicates = false, CancellationToken cancellationToken = default)
        {
            Calls.Add($"add {string.Join(',', videoIds)}{(allowDuplicates ? " (duplicates)" : string.Empty)}");
            IReadOnlyList<PlaylistEntryRef> added = ReportIds
                ? [.. videoIds.Where(v => ReportOnly is null || v == ReportOnly).Select((v, i) => new PlaylistEntryRef(v, "N" + i))]
                : [];
            return Task.FromResult(added);
        }

        public Task MovePlaylistItemAsync(string playlistId, string setVideoId, string? successorSetVideoId = null, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("failed");
            }

            Calls.Add(successorSetVideoId is null ? $"move {setVideoId} to end" : $"move {setVideoId} before {successorSetVideoId}");
            return Task.CompletedTask;
        }

        public Task RemovePlaylistItemsAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<AccountInfo?> GetAccountInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RateSongAsync(string videoId, LikeStatus status, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<string> CreatePlaylistAsync(string title, string? description, PrivacyStatus privacy, IReadOnlyList<string>? videoIds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task EditPlaylistAsync(string playlistId, string? title = null, string? description = null, PrivacyStatus? privacy = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task AddHistoryItemAsync(string videoId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
