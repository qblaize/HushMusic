using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;
using HushMusic.Core.Services;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>What the sort menu calls a list's own order, and its two directions.</summary>
public sealed record TrackListOwnOrder(string Name, string Forward, string Backward)
{
    public static TrackListOwnOrder Playlist { get; } = new("Custom order", "Playlist order", "Reversed");

    /// <summary>Liked songs come newest first.</summary>
    public static TrackListOwnOrder LikedSongs { get; } = new("Recently liked", "Newest first", "Oldest first");
}

/// <summary>
/// The filter box and sort menu above a page's track list, and the rows the list shows (<see cref="Rows"/>).
/// Unfiltered and in its own order, the rows follow the page's list one to one: they load page by page as the list
/// scrolls, and a row dragged to a new place moves in the page's list too. Filtered or sorted, they cover the whole list
/// (the rest of it is loaded first), and songs added, removed or put back later show up in them. UI thread only.
/// </summary>
/// <remarks>
/// The rows are one collection for the life of the page, changed row by row. A ListView whose ItemsSource is replaced
/// or reset moves keyboard focus to its first focusable element, which would take it away from the filter box (in the
/// list's header) while the user types.
/// </remarks>
public sealed partial class TrackListFilter : ObservableObject
{
    private static readonly TimeSpan FilterDelay = TimeSpan.FromMilliseconds(150);

    private readonly IncrementalCollection<TrackItem> _source;
    private readonly Func<CancellationToken> _lifetime;
    private readonly TrackListOwnOrder _ownOrder;
    private readonly TrackListView<TrackItem> _view = new(item => item.Track);
    private readonly TrackRowList _rows;
    private TrackListOptions _options = TrackListOptions.Default;
    private string _filterText = string.Empty;
    private CancellationTokenSource? _filterDelay;
    private TaskCompletionSource? _sourceIdle;
    private Task _loadingRest = Task.CompletedTask;
    private bool _updateQueued;
    private bool _changingRows;
    private bool _changingSource;

    /// <param name="source">The page's list, in its own order.</param>
    /// <param name="lifetime">Cancels loading the rest of the list (the page's navigation token).</param>
    /// <param name="ownOrder">The sort menu's name for the list's own order.</param>
    public TrackListFilter(IncrementalCollection<TrackItem> source, Func<CancellationToken> lifetime, TrackListOwnOrder ownOrder)
    {
        _source = source;
        _lifetime = lifetime;
        _ownOrder = ownOrder;
        _rows = new TrackRowList(() => !IsActive && _source.HasMoreItems, source.LoadMoreItemsAsync);
        _rows.CollectionChanged += OnRowsChanged;
        source.CollectionChanged += OnSourceChanged;
        ((INotifyPropertyChanged)source).PropertyChanged += OnSourcePropertyChanged;
    }

    /// <summary>Raised when the filter or the sort changed what the rows show (so a selection can drop hidden songs).</summary>
    public event EventHandler? Arranged;

    /// <summary>The filter box (two-way); the rows follow after a short pause in typing.</summary>
    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    public TrackSort Sort { get; private set; }

    public bool Descending { get; private set; }

    /// <summary>What the list shows, in order: the list's ItemsSource.</summary>
    public IList<TrackItem> Rows => _rows;

    /// <summary>The rows are filtered or sorted (so they can't be dragged into a new order).</summary>
    public bool IsActive => !_options.IsDefault;

    /// <summary>Only the songs matching the filter are shown.</summary>
    public bool IsFiltered => _options.IsFiltered;

    /// <summary>Loading the rest of the list for the filter or sort.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLoadingRows), nameof(ShowNoMatches))]
    public partial bool IsLoadingRest { get; private set; }

    /// <summary>More rows are on their way (the list footer's placeholder rows).</summary>
    public bool ShowLoadingRows => IsLoadingRest || _source.IsLoading;

    public bool ShowNoMatches =>
        IsFiltered && _rows.Count == 0 && !IsLoadingRest && !_source.IsLoading && !_source.HasMoreItems;

    public string NoMatchesText => $"No songs match “{_filterText.Trim()}”";

    /// <summary>The sort menu's name for the list's own order ("Custom order").</summary>
    public string OwnOrderName => _ownOrder.Name;

    /// <summary>The sort button's label: what the list is sorted by.</summary>
    public string SortLabel => Sort switch
    {
        TrackSort.Title => "Title",
        TrackSort.Artist => "Artist",
        TrackSort.Album => "Album",
        TrackSort.Duration => "Duration",
        _ => _ownOrder.Name,
    };

    public string AscendingText => Sort switch
    {
        TrackSort.Custom => _ownOrder.Forward,
        TrackSort.Duration => "Shortest first",
        _ => "A to Z",
    };

    public string DescendingText => Sort switch
    {
        TrackSort.Custom => _ownOrder.Backward,
        TrackSort.Duration => "Longest first",
        _ => "Z to A",
    };

    /// <summary>An arrow on the sort button for the direction; none for the list's own order.</summary>
    public string DirectionGlyph => !_options.IsSorted ? string.Empty : Descending ? "" : "";

    /// <summary>The sort button's accessible name and tooltip, e.g. "Sort: Title, A to Z".</summary>
    public string SortDescription => Sort == TrackSort.Custom && !Descending
        ? $"Sort: {SortLabel}"
        : $"Sort: {SortLabel}, {(Descending ? DescendingText : AscendingText)}";

    /// <summary>Set by pages whose list can otherwise be dragged into a new order: shows <see cref="ReorderHint"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReorderHint))]
    public partial bool OffersReorder { get; set; }

    public string ReorderHint => !OffersReorder || !IsActive
        ? string.Empty
        : (_options.IsFiltered, _options.IsSorted) switch
        {
            (true, true) => $"To reorder songs, clear the filter and choose {_ownOrder.Name}.",
            (false, true) => $"To reorder songs, choose {_ownOrder.Name}.",
            _ => "To reorder songs, clear the filter.",
        };

    /// <summary>Sorts by <paramref name="sort"/>, in its first direction (A to Z, shortest first, the list's order).</summary>
    public void SortBy(TrackSort sort)
    {
        Sort = sort;
        Descending = false;
        Apply();
    }

    public void SetDescending(bool descending)
    {
        Descending = descending;
        Apply();
    }

    /// <summary>
    /// Completes once the rows cover the whole list (at once when they are unfiltered and in the list's order), so
    /// playing them plays everything that matches, in order.
    /// </summary>
    public Task WhenCompleteAsync()
    {
        StartLoadingRest();
        return _loadingRest;
    }

    [RelayCommand]
    private void ClearFilter() => Text = string.Empty;

    partial void OnTextChanged(string value)
    {
        _filterDelay?.Cancel();
        _filterDelay?.Dispose();
        _filterDelay = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            // Clearing shows the whole list right away.
            _filterText = string.Empty;
            Apply();
            return;
        }

        var delay = new CancellationTokenSource();
        _filterDelay = delay;
        _ = ApplyFilterAsync(value, delay.Token);
    }

    private async Task ApplyFilterAsync(string text, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(FilterDelay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _filterText = text;
        Apply();
    }

    private void Apply()
    {
        var options = new TrackListOptions(string.Join(' ', TrackListView.Terms(_filterText)), Sort, Descending);
        if (options != _options)
        {
            _options = options;
            ChangeRows(rows => rows.SyncTo(IsActive ? Arrange() : _source));
            StartLoadingRest();
            Arranged?.Invoke(this, EventArgs.Empty);
        }

        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsFiltered));
        OnPropertyChanged(nameof(SortLabel));
        OnPropertyChanged(nameof(AscendingText));
        OnPropertyChanged(nameof(DescendingText));
        OnPropertyChanged(nameof(DirectionGlyph));
        OnPropertyChanged(nameof(SortDescription));
        OnPropertyChanged(nameof(ReorderHint));
        OnPropertyChanged(nameof(NoMatchesText));
        OnPropertyChanged(nameof(ShowNoMatches));
    }

    // While the rest of the list loads, a sort waits for it (otherwise the rows would jump around with every page that
    // arrives); the filter applies right away.
    private List<TrackItem> Arrange()
    {
        var options = _options.IsSorted && _source.HasMoreItems ? _options with { Sort = TrackSort.Custom, Descending = false } : _options;
        return _view.Apply(_source, options);
    }

    private void Update()
    {
        if (IsActive)
        {
            ChangeRows(rows => rows.SyncTo(Arrange()));
            OnPropertyChanged(nameof(ShowNoMatches));
        }
    }

    private void ChangeRows(Action<TrackRowList> change)
    {
        _changingRows = true;
        try
        {
            change(_rows);
        }
        finally
        {
            _changingRows = false;
        }
    }

    private void StartLoadingRest()
    {
        if (!IsLoadingRest && IsActive && _source.HasMoreItems)
        {
            _loadingRest = LoadRestAsync();
        }
    }

    private async Task LoadRestAsync()
    {
        IsLoadingRest = true;
        try
        {
            var lifetime = _lifetime();
            var emptyPages = 0;
            while (IsActive && _source.HasMoreItems && !lifetime.IsCancellationRequested && emptyPages < 3)
            {
                if (_source.IsLoading)
                {
                    // The list is already fetching a page (it was scrolled to the end just before).
                    _sourceIdle ??= new TaskCompletionSource();
                    await _sourceIdle.Task.WaitAsync(lifetime);
                    continue;
                }

                emptyPages = await _source.LoadMoreAsync() > 0 ? 0 : emptyPages + 1;
            }
        }
        catch (OperationCanceledException)
        {
            // Left the page.
        }
        finally
        {
            IsLoadingRest = false;
            Update();
        }
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_changingSource)
        {
            return;
        }

        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // A reload: the rows start over (a selection ends with them).
            ChangeRows(rows => rows.ReplaceAll(IsActive ? Arrange() : _source));
            QueueUpdate();
        }
        else if (!IsActive)
        {
            ChangeRows(rows => rows.Mirror(e, _source));
        }
        else if (e.Action == NotifyCollectionChangedAction.Remove && e.OldItems is not null)
        {
            ChangeRows(rows =>
            {
                foreach (TrackItem item in e.OldItems)
                {
                    rows.Remove(item);
                }
            });
            OnPropertyChanged(nameof(ShowNoMatches));
        }
        else if (e.Action == NotifyCollectionChangedAction.Replace && e.OldItems is not null && e.NewItems is not null)
        {
            // Undo put a song back as a new playlist entry: same song, same place.
            ChangeRows(rows =>
            {
                for (var i = 0; i < e.OldItems.Count && i < e.NewItems.Count; i++)
                {
                    if (e.OldItems[i] is TrackItem old && e.NewItems[i] is TrackItem replacement && rows.IndexOf(old) is >= 0 and var index)
                    {
                        rows[index] = replacement;
                    }
                }
            });
        }
        else
        {
            // Pages arrive one row at a time: the rows catch up once per page.
            QueueUpdate();
        }
    }

    // The list moved a row itself (drag to reorder, only offered while the rows follow the page's list): the page's list
    // makes the same move, so the page saves it from there.
    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_changingRows || IsActive)
        {
            return;
        }

        _changingSource = true;
        try
        {
            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Remove when e.OldStartingIndex >= 0 && e.OldStartingIndex < _source.Count:
                    _source.RemoveAt(e.OldStartingIndex);
                    break;
                case NotifyCollectionChangedAction.Add when e.NewItems is { Count: > 0 } added && added[0] is TrackItem item && e.NewStartingIndex >= 0:
                    _source.Insert(Math.Min(e.NewStartingIndex, _source.Count), item);
                    break;
                case NotifyCollectionChangedAction.Move when e.OldStartingIndex >= 0 && e.NewStartingIndex >= 0:
                    _source.Move(e.OldStartingIndex, e.NewStartingIndex);
                    break;
            }
        }
        finally
        {
            _changingSource = false;
        }
    }

    private void QueueUpdate()
    {
        if (!_updateQueued)
        {
            _updateQueued = true;
            _ = UpdateSoonAsync();
        }
    }

    private async Task UpdateSoonAsync()
    {
        await Task.Yield();
        _updateQueued = false;
        Update();

        // A reload starts again from the first page.
        StartLoadingRest();
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(IncrementalCollection<TrackItem>.IsLoading) or nameof(IncrementalCollection<TrackItem>.HasMoreItems)))
        {
            return;
        }

        if (!_source.IsLoading && _sourceIdle is { } idle)
        {
            _sourceIdle = null;
            idle.TrySetResult();
        }

        OnPropertyChanged(nameof(ShowLoadingRows));
        OnPropertyChanged(nameof(ShowNoMatches));
    }
}

/// <summary>
/// The rows a track list shows. Changed row by row (<see cref="SyncTo"/>, <see cref="Mirror"/>); only a reload replaces
/// them all at once. Loads more on demand through the page's list while it follows that list.
/// </summary>
internal sealed partial class TrackRowList(Func<bool> hasMoreItems, Func<uint, IAsyncOperation<LoadMoreItemsResult>> loadMore)
    : ObservableCollection<TrackItem>, ISupportIncrementalLoading
{
    public bool HasMoreItems => hasMoreItems();

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count) => loadMore(count);

    public void ReplaceAll(IEnumerable<TrackItem> rows)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var row in rows)
        {
            Items.Add(row);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Removes, inserts and moves rows (as remove + insert) until they are <paramref name="target"/>.</summary>
    public void SyncTo(IReadOnlyList<TrackItem> target)
    {
        var wanted = new HashSet<TrackItem>(target, ReferenceEqualityComparer.Instance);
        for (var i = Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(this[i]))
            {
                RemoveAt(i);
            }
        }

        var present = new HashSet<TrackItem>(this, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < target.Count; i++)
        {
            var row = target[i];
            if (i < Count && ReferenceEquals(this[i], row))
            {
                continue;
            }

            if (present.Contains(row) && IndexOf(row, i + 1) is >= 0 and var from)
            {
                RemoveAt(from);
            }

            Insert(i, row);
        }
    }

    /// <summary>Makes the change <paramref name="e"/> made to <paramref name="source"/>, whose rows these follow.</summary>
    public void Mirror(NotifyCollectionChangedEventArgs e, IList<TrackItem> source)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null && e.NewStartingIndex >= 0 && e.NewStartingIndex <= Count:
                for (var i = 0; i < e.NewItems.Count; i++)
                {
                    Insert(e.NewStartingIndex + i, (TrackItem)e.NewItems[i]!);
                }

                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null && e.OldStartingIndex >= 0 && e.OldStartingIndex + e.OldItems.Count <= Count:
                for (var i = 0; i < e.OldItems.Count; i++)
                {
                    RemoveAt(e.OldStartingIndex);
                }

                break;
            case NotifyCollectionChangedAction.Replace when e.NewItems is not null && e.NewStartingIndex >= 0 && e.NewStartingIndex + e.NewItems.Count <= Count:
                for (var i = 0; i < e.NewItems.Count; i++)
                {
                    this[e.NewStartingIndex + i] = (TrackItem)e.NewItems[i]!;
                }

                break;
            default:
                SyncTo([.. source]);
                return;
        }

        if (Count != source.Count)
        {
            SyncTo([.. source]);
        }
    }

    private int IndexOf(TrackItem row, int start)
    {
        for (var i = start; i < Count; i++)
        {
            if (ReferenceEquals(this[i], row))
            {
                return i;
            }
        }

        return -1;
    }
}
