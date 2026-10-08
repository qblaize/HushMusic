using HushMusic.Core.Abstractions;
using HushMusic.Core.Queue;
using Xunit;
using static HushMusic.Core.Tests.TestData;

namespace HushMusic.Core.Tests;

public sealed class QueueServiceTests
{
    private readonly QueueService _queue = new();
    private readonly List<QueueChangeKind> _changes = [];
    private readonly List<QueueCurrentChangedEventArgs> _currentChanges = [];

    public QueueServiceTests()
    {
        _queue.Changed += (_, e) => _changes.Add(e.Kind);
        _queue.CurrentChanged += (_, e) => _currentChanges.Add(e);
    }

    [Fact]
    public void New_queue_is_empty()
    {
        Assert.Empty(_queue.Items);
        Assert.Equal(-1, _queue.CurrentIndex);
        Assert.Null(_queue.Current);
        Assert.Null(_queue.PeekNext());
        Assert.Null(_queue.MoveNext(userInitiated: false));
        Assert.Null(_queue.MovePrevious());
    }

    [Fact]
    public void Load_sets_items_cursor_source_and_continuation()
    {
        var source = new QueueSource(QueueSourceKind.Playlist, "PL1", "Mix");
        _queue.Load(Tracks("a", "b", "c"), startIndex: 1, source, "token");

        Assert.Equal(["a", "b", "c"], Ids(_queue.Items));
        Assert.Equal(1, _queue.CurrentIndex);
        Assert.Equal("b", _queue.Current?.Track.VideoId);
        Assert.Same(source, _queue.Source);
        Assert.Equal("token", _queue.Continuation);
        Assert.Equal([QueueChangeKind.Reset], _changes);
        var current = Assert.Single(_currentChanges);
        Assert.Null(current.Previous);
        Assert.Equal("b", current.Current?.Track.VideoId);
        Assert.Equal(1, current.CurrentIndex);
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(99, 2)]
    public void Load_clamps_the_start_index(int startIndex, int expected)
    {
        _queue.Load(Tracks("a", "b", "c"), startIndex);
        Assert.Equal(expected, _queue.CurrentIndex);
    }

    [Fact]
    public void Load_with_no_tracks_leaves_the_queue_empty()
    {
        _queue.Load([]);
        Assert.Equal(-1, _queue.CurrentIndex);
        Assert.Empty(_currentChanges);
        Assert.Equal([QueueChangeKind.Reset], _changes);
    }

    [Fact]
    public void Queue_items_get_unique_ids_even_for_the_same_track()
    {
        var track = Track("a");
        _queue.Load([track, track]);
        Assert.NotEqual(_queue.Items[0].Id, _queue.Items[1].Id);
    }

    [Fact]
    public void Items_is_a_snapshot()
    {
        _queue.Load(Tracks("a", "b"));
        var snapshot = _queue.Items;
        _queue.Enqueue(Tracks("c"));

        Assert.Equal(["a", "b"], Ids(snapshot));
        Assert.Equal(["a", "b", "c"], Ids(_queue.Items));
    }

    [Fact]
    public void MoveNext_advances_and_stops_at_the_end_when_repeat_is_off()
    {
        _queue.Load(Tracks("a", "b"));
        _currentChanges.Clear();

        Assert.Equal("b", _queue.MoveNext(userInitiated: false)?.Track.VideoId);
        Assert.Null(_queue.MoveNext(userInitiated: false));
        Assert.Null(_queue.MoveNext(userInitiated: true));
        Assert.Equal(1, _queue.CurrentIndex);
        Assert.Single(_currentChanges);
    }

    [Fact]
    public void MoveNext_wraps_with_repeat_all()
    {
        _queue.Load(Tracks("a", "b"), startIndex: 1);
        _queue.RepeatMode = RepeatMode.All;

        Assert.Equal("a", _queue.MoveNext(userInitiated: false)?.Track.VideoId);
        Assert.Equal(0, _queue.CurrentIndex);
    }

    [Fact]
    public void Repeat_one_stays_on_automatic_advance_but_moves_on_user_next()
    {
        _queue.Load(Tracks("a", "b"));
        _queue.RepeatMode = RepeatMode.One;
        _currentChanges.Clear();

        var same = _queue.MoveNext(userInitiated: false);
        Assert.Equal("a", same?.Track.VideoId);
        Assert.Equal(0, _queue.CurrentIndex);
        Assert.Empty(_currentChanges);

        Assert.Equal("b", _queue.MoveNext(userInitiated: true)?.Track.VideoId);

        // A user "next" at the end wraps around: repeat is on.
        Assert.Equal("a", _queue.MoveNext(userInitiated: true)?.Track.VideoId);
    }

    [Fact]
    public void MovePrevious_goes_back_and_stops_at_the_start_when_repeat_is_off()
    {
        _queue.Load(Tracks("a", "b"), startIndex: 1);

        Assert.Equal("a", _queue.MovePrevious()?.Track.VideoId);
        Assert.Null(_queue.MovePrevious());
        Assert.Equal(0, _queue.CurrentIndex);
    }

    [Fact]
    public void MovePrevious_wraps_to_the_end_with_repeat()
    {
        _queue.Load(Tracks("a", "b", "c"));
        _queue.RepeatMode = RepeatMode.All;

        Assert.Equal("c", _queue.MovePrevious()?.Track.VideoId);
        Assert.Equal(2, _queue.CurrentIndex);
    }

    [Theory]
    [InlineData(RepeatMode.Off, 0, "b")]
    [InlineData(RepeatMode.Off, 2, null)]
    [InlineData(RepeatMode.All, 2, "a")]
    [InlineData(RepeatMode.One, 1, "b")]
    public void PeekNext_matches_automatic_MoveNext_without_moving(RepeatMode mode, int start, string? expected)
    {
        _queue.Load(Tracks("a", "b", "c"), start);
        _queue.RepeatMode = mode;

        Assert.Equal(expected, _queue.PeekNext()?.Track.VideoId);
        Assert.Equal(start, _queue.CurrentIndex);
        Assert.Equal(expected, _queue.MoveNext(userInitiated: false)?.Track.VideoId);
    }

    [Fact]
    public void MoveTo_moves_the_cursor_and_rejects_bad_indices()
    {
        _queue.Load(Tracks("a", "b", "c"));
        _currentChanges.Clear();

        Assert.True(_queue.MoveTo(2));
        Assert.Equal("c", _queue.Current?.Track.VideoId);
        Assert.Single(_currentChanges);

        Assert.True(_queue.MoveTo(2));
        Assert.Single(_currentChanges);

        Assert.False(_queue.MoveTo(3));
        Assert.False(_queue.MoveTo(-1));
        Assert.Equal(2, _queue.CurrentIndex);
    }

    [Fact]
    public void Enqueue_appends_without_moving_the_cursor()
    {
        _queue.Load(Tracks("a", "b"));
        _changes.Clear();
        _currentChanges.Clear();

        _queue.Enqueue(Tracks("c", "d"));

        Assert.Equal(["a", "b", "c", "d"], Ids(_queue.Items));
        Assert.Equal(0, _queue.CurrentIndex);
        Assert.Equal([QueueChangeKind.Added], _changes);
        Assert.Empty(_currentChanges);
    }

    [Fact]
    public void Enqueue_on_an_empty_queue_makes_the_first_item_current()
    {
        _queue.Enqueue(Tracks("a", "b"));

        Assert.Equal(0, _queue.CurrentIndex);
        Assert.Equal("a", Assert.Single(_currentChanges).Current?.Track.VideoId);
    }

    [Fact]
    public void Enqueue_with_no_tracks_does_nothing()
    {
        _queue.Load(Tracks("a"));
        _changes.Clear();

        _queue.Enqueue([]);
        _queue.EnqueueNext([]);

        Assert.Empty(_changes);
    }

    [Fact]
    public void EnqueueNext_inserts_right_after_the_current_item_in_order()
    {
        _queue.Load(Tracks("a", "b", "c"), startIndex: 1);

        _queue.EnqueueNext(Tracks("x", "y"));

        Assert.Equal(["a", "b", "x", "y", "c"], Ids(_queue.Items));
        Assert.Equal(1, _queue.CurrentIndex);
        Assert.Equal("x", _queue.PeekNext()?.Track.VideoId);
    }

    [Fact]
    public void AppendContinuation_appends_and_replaces_the_token()
    {
        _queue.Load(Tracks("a"), continuation: "t1");
        _changes.Clear();

        _queue.AppendContinuation(Tracks("b", "c"), "t2");

        Assert.Equal(["a", "b", "c"], Ids(_queue.Items));
        Assert.Equal("t2", _queue.Continuation);
        Assert.Equal([QueueChangeKind.Added], _changes);
    }

    [Fact]
    public void AppendContinuation_with_no_tracks_only_updates_the_token()
    {
        _queue.Load(Tracks("a"), continuation: "t1");
        _changes.Clear();

        _queue.AppendContinuation([], null);

        Assert.Null(_queue.Continuation);
        Assert.Single(_queue.Items);
        Assert.Empty(_changes);
    }

    [Fact]
    public void RemoveAt_before_the_current_item_shifts_the_cursor()
    {
        _queue.Load(Tracks("a", "b", "c"), startIndex: 2);
        _currentChanges.Clear();

        _queue.RemoveAt(0);

        Assert.Equal(["b", "c"], Ids(_queue.Items));
        Assert.Equal(1, _queue.CurrentIndex);
        Assert.Equal("c", _queue.Current?.Track.VideoId);
        var change = Assert.Single(_currentChanges);
        Assert.Same(change.Previous, change.Current);
        Assert.Equal(1, change.CurrentIndex);
    }

    [Fact]
    public void RemoveAt_after_the_current_item_keeps_the_cursor()
    {
        _queue.Load(Tracks("a", "b", "c"));
        _currentChanges.Clear();

        _queue.RemoveAt(2);

        Assert.Equal(["a", "b"], Ids(_queue.Items));
        Assert.Equal(0, _queue.CurrentIndex);
        Assert.Empty(_currentChanges);
        Assert.Contains(QueueChangeKind.Removed, _changes);
    }

    [Fact]
    public void RemoveAt_the_current_item_makes_the_following_item_current()
    {
        _queue.Load(Tracks("a", "b", "c"), startIndex: 1);
        _currentChanges.Clear();

        _queue.RemoveAt(1);

        Assert.Equal(1, _queue.CurrentIndex);
        Assert.Equal("c", _queue.Current?.Track.VideoId);
        var change = Assert.Single(_currentChanges);
        Assert.Equal("b", change.Previous?.Track.VideoId);
        Assert.Equal("c", change.Current?.Track.VideoId);
    }

    [Fact]
    public void RemoveAt_the_current_last_item_falls_back_to_the_new_last_item()
    {
        _queue.Load(Tracks("a", "b"), startIndex: 1);

        _queue.RemoveAt(1);

        Assert.Equal(0, _queue.CurrentIndex);
        Assert.Equal("a", _queue.Current?.Track.VideoId);
    }

    [Fact]
    public void RemoveAt_the_only_item_empties_the_queue()
    {
        _queue.Load(Tracks("a"));
        _currentChanges.Clear();

        _queue.RemoveAt(0);

        Assert.Empty(_queue.Items);
        Assert.Equal(-1, _queue.CurrentIndex);
        Assert.Null(Assert.Single(_currentChanges).Current);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void RemoveAt_ignores_out_of_range_indices(int index)
    {
        _queue.Load(Tracks("a", "b", "c"));
        _changes.Clear();

        _queue.RemoveAt(index);

        Assert.Equal(3, _queue.Items.Count);
        Assert.Empty(_changes);
    }

    [Theory]
    [InlineData(2, 2, 0, new[] { "c", "a", "b", "d", "e" }, 0)] // current item to the start
    [InlineData(2, 2, 4, new[] { "a", "b", "d", "e", "c" }, 4)] // current item to the end
    [InlineData(2, 0, 3, new[] { "b", "c", "d", "a", "e" }, 1)] // before current -> after current
    [InlineData(2, 4, 1, new[] { "a", "e", "b", "c", "d" }, 3)] // after current -> before current
    [InlineData(2, 3, 4, new[] { "a", "b", "c", "e", "d" }, 2)] // both after current
    public void Move_reorders_and_keeps_the_cursor_on_the_same_item(int start, int from, int to, string[] expected, int expectedIndex)
    {
        _queue.Load(Tracks("a", "b", "c", "d", "e"), start);
        var current = _queue.Current;

        _queue.Move(from, to);

        Assert.Equal(expected, Ids(_queue.Items));
        Assert.Equal(expectedIndex, _queue.CurrentIndex);
        Assert.Same(current, _queue.Current);
        Assert.Equal(QueueChangeKind.Moved, _changes[^1]);
    }

    [Fact]
    public void Move_ignores_out_of_range_and_no_op_moves()
    {
        _queue.Load(Tracks("a", "b"));
        _changes.Clear();

        _queue.Move(0, 5);
        _queue.Move(-1, 0);
        _queue.Move(1, 1);

        Assert.Equal(["a", "b"], Ids(_queue.Items));
        Assert.Empty(_changes);
    }

    [Fact]
    public void Clear_resets_everything()
    {
        _queue.Load(Tracks("a", "b"), source: new QueueSource(QueueSourceKind.Radio), continuation: "t");
        _queue.SetShuffle(true);
        _changes.Clear();
        _currentChanges.Clear();

        _queue.Clear();

        Assert.Empty(_queue.Items);
        Assert.Equal(-1, _queue.CurrentIndex);
        Assert.Null(_queue.Source);
        Assert.Null(_queue.Continuation);
        Assert.False(_queue.IsShuffled);
        Assert.Equal([QueueChangeKind.Cleared], _changes);
        Assert.Null(Assert.Single(_currentChanges).Current);
    }

    [Fact]
    public void Shuffle_puts_the_current_item_first_and_shuffles_the_rest()
    {
        var ids = Enumerable.Range(0, 50).Select(i => "v" + i).ToArray();
        _queue.Load(Tracks(ids), startIndex: 20);
        var current = _queue.Current;

        _queue.SetShuffle(true);

        Assert.True(_queue.IsShuffled);
        Assert.Equal(0, _queue.CurrentIndex);
        Assert.Same(current, _queue.Current);
        Assert.Equal(ids.Order(), Ids(_queue.Items).Order());
        Assert.NotEqual(ids.Where(id => id != "v20"), Ids(_queue.Items).Skip(1)); // 1 in 49! chance of a false failure
        Assert.Equal(QueueChangeKind.Shuffled, _changes[^1]);
    }

    [Fact]
    public void Unshuffle_restores_the_original_order_and_finds_the_current_item()
    {
        var ids = Enumerable.Range(0, 20).Select(i => "v" + i).ToArray();
        _queue.Load(Tracks(ids), startIndex: 5);
        _queue.SetShuffle(true);
        _queue.MoveNext(userInitiated: true);
        _queue.MoveNext(userInitiated: true);
        var current = _queue.Current!;

        _queue.SetShuffle(false);

        Assert.False(_queue.IsShuffled);
        Assert.Equal(ids, Ids(_queue.Items));
        Assert.Same(current, _queue.Current);
        Assert.Equal(Array.IndexOf(ids, current.Track.VideoId), _queue.CurrentIndex);
    }

    [Fact]
    public void Unshuffle_keeps_items_added_and_drops_items_removed_while_shuffled()
    {
        _queue.Load(Tracks("a", "b", "c", "d"), startIndex: 1);
        _queue.SetShuffle(true);

        _queue.Enqueue(Tracks("end"));
        _queue.EnqueueNext(Tracks("next"));
        _queue.AppendContinuation(Tracks("more"), null);
        _queue.RemoveAt(_queue.Items.ToList().FindIndex(i => i.Track.VideoId == "d"));

        _queue.SetShuffle(false);

        Assert.Equal(["a", "b", "next", "c", "end", "more"], Ids(_queue.Items));
        Assert.Equal("b", _queue.Current?.Track.VideoId);
        Assert.Equal(1, _queue.CurrentIndex);
    }

    [Fact]
    public void SetShuffle_to_the_current_state_does_nothing()
    {
        _queue.Load(Tracks("a", "b"));
        _changes.Clear();

        _queue.SetShuffle(false);

        Assert.Empty(_changes);
    }

    [Fact]
    public void Load_turns_shuffle_off()
    {
        _queue.Load(Tracks("a", "b"));
        _queue.SetShuffle(true);

        _queue.Load(Tracks("c", "d"));

        Assert.False(_queue.IsShuffled);
        Assert.Equal(["c", "d"], Ids(_queue.Items));
    }

    [Fact]
    public void Events_are_raised_after_the_lock_is_released()
    {
        _queue.Load(Tracks("a", "b"));
        var otherThreadCompleted = false;
        _queue.Changed += (_, _) =>
            otherThreadCompleted = Task.Run(() => _queue.Items.Count).Wait(TimeSpan.FromSeconds(5));

        _queue.Enqueue(Tracks("c"));

        Assert.True(otherThreadCompleted);
    }

    [Fact]
    public void Handlers_can_change_the_queue_reentrantly()
    {
        _queue.Load(Tracks("a"));
        _queue.CurrentChanged += (_, e) =>
        {
            if (e.Current?.Track.VideoId == "b")
            {
                _queue.Enqueue(Tracks("c"));
            }
        };

        _queue.Enqueue(Tracks("b"));
        _queue.MoveNext(userInitiated: true);

        Assert.Equal(["a", "b", "c"], Ids(_queue.Items));
    }

    [Fact]
    public async Task Concurrent_changes_are_not_lost()
    {
        _queue.Load(Tracks("start"));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(
            () =>
            {
                for (var i = 0; i < 100; i++)
                {
                    _queue.Enqueue(Tracks($"{t}-{i}"));
                    _ = _queue.PeekNext();
                }
            },
            TestContext.Current.CancellationToken)));

        Assert.Equal(801, _queue.Items.Count);
        Assert.Equal(801, _queue.Items.Select(i => i.Id).Distinct().Count());
    }
}
