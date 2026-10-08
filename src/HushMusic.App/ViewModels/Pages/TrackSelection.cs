using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>Implemented by page view models whose track list supports multi-select (track menus look for it).</summary>
public interface ITrackSelectionHost
{
    TrackSelection Selection { get; }
}

/// <summary>
/// Multi-select for a page's track list: a "Select" mode with check marks, plus Ctrl-click (toggle) and Shift-click (range).
/// The bottom selection bar acts on <see cref="SelectedItems"/>: play, play next, add to queue, add to playlist, and remove
/// from the playlist when the page allows it. UI thread only.
/// </summary>
public sealed partial class TrackSelection : ObservableObject
{
    private readonly IList<TrackItem> _items;
    private readonly IMediaItemActions _actions;
    private readonly Func<QueueSource?> _source;
    private readonly Func<IReadOnlyList<TrackItem>, Task>? _remove;
    private readonly Func<bool> _canRemove;
    private readonly HashSet<TrackItem> _selected = new(ReferenceEqualityComparer.Instance);
    private TrackItem? _anchor;

    // Select mode entered with the "Select" button stays on when nothing is selected; Ctrl/Shift-click mode ends with the
    // last selected song.
    private bool _isExplicit;

    /// <param name="items">The page's list, in display order.</param>
    /// <param name="source">What the selection plays as (the album, the playlist...).</param>
    /// <param name="remove">Removes songs from the page's playlist; null where that doesn't apply.</param>
    /// <param name="canRemove">Whether removing is possible right now (the user owns the playlist).</param>
    public TrackSelection(
        IList<TrackItem> items,
        IMediaItemActions actions,
        Func<QueueSource?> source,
        Func<IReadOnlyList<TrackItem>, Task>? remove = null,
        Func<bool>? canRemove = null)
    {
        _items = items;
        _actions = actions;
        _source = source;
        _remove = remove;
        _canRemove = canRemove ?? (() => remove is not null);
        if (items is INotifyCollectionChanged observable)
        {
            observable.CollectionChanged += OnItemsChanged;
        }
    }

    /// <summary>Raised whenever an item was selected or deselected, or select mode turned on or off.</summary>
    public event EventHandler? Changed;

    /// <summary>Select mode: rows show check marks and a click selects instead of playing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemove))]
    [NotifyPropertyChangedFor(nameof(ModeButtonText))]
    public partial bool IsActive { get; private set; }

    /// <summary>The header button's label: "Select", or "Done" while selecting.</summary>
    public string ModeButtonText => IsActive ? "Done" : "Select";

    public int Count => _selected.Count;

    public bool HasSelection => _selected.Count > 0;

    public string Summary => _selected.Count switch
    {
        0 => "Select songs",
        1 => "1 song selected",
        var n => $"{n} songs selected",
    };

    public bool IsSignedIn => _actions.IsSignedIn;

    /// <summary>The page offers "Remove from this playlist" (shown even while nothing is selected).</summary>
    public bool CanRemove => _remove is not null && _canRemove();

    /// <summary>Selected items in list order.</summary>
    public IReadOnlyList<TrackItem> SelectedItems => [.. _items.Where(_selected.Contains)];

    public bool IsSelected(TrackItem item) => _selected.Contains(item);

    /// <summary>
    /// A row was clicked. Returns true when the click was a selection gesture (Ctrl toggles, Shift selects a range, any click
    /// in select mode toggles); false means the page should play the song as usual.
    /// </summary>
    public bool HandleClick(TrackItem item, bool control, bool shift)
    {
        if (shift && (IsActive || control))
        {
            SelectRange(item, keepOthers: control);
            return true;
        }

        if (!control && !shift && !IsActive)
        {
            return false;
        }

        Toggle(item);
        return true;
    }

    public void Toggle(TrackItem item)
    {
        if (!_selected.Remove(item))
        {
            _selected.Add(item);
        }

        _anchor = item;
        IsActive = true;
        AfterChange();
    }

    /// <summary>Selects every item from the last clicked one to <paramref name="item"/>.</summary>
    public void SelectRange(TrackItem item, bool keepOthers = false)
    {
        var to = _items.IndexOf(item);
        if (to < 0)
        {
            return;
        }

        var from = _anchor is { } anchor && _items.IndexOf(anchor) is >= 0 and var index ? index : to;
        if (!keepOthers)
        {
            _selected.Clear();
        }

        for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
        {
            _selected.Add(_items[i]);
        }

        _anchor ??= item;
        IsActive = true;
        AfterChange();
    }

    /// <summary>Turns select mode on (the header's "Select" button).</summary>
    [RelayCommand]
    public void Enter()
    {
        _isExplicit = true;
        IsActive = true;
        AfterChange();
    }

    /// <summary>The header button: enters select mode, or leaves it.</summary>
    [RelayCommand]
    public void ToggleMode()
    {
        if (IsActive)
        {
            Exit();
        }
        else
        {
            Enter();
        }
    }

    /// <summary>Clears the selection and leaves select mode.</summary>
    [RelayCommand]
    public void Exit()
    {
        _selected.Clear();
        _anchor = null;
        _isExplicit = false;
        IsActive = false;
        AfterChange();
    }

    [RelayCommand]
    public void SelectAll()
    {
        foreach (var item in _items)
        {
            _selected.Add(item);
        }

        IsActive = true;
        AfterChange();
    }

    /// <summary>Drops items that left the list (removed songs).</summary>
    public void Forget(IEnumerable<TrackItem> items)
    {
        var changed = false;
        foreach (var item in items)
        {
            changed |= _selected.Remove(item);
        }

        if (changed)
        {
            AfterChange();
        }
    }

    [RelayCommand]
    private Task PlayAsync()
    {
        if (SelectedTracks() is not { Count: > 0 } tracks)
        {
            return Task.CompletedTask;
        }

        Exit();
        return _actions.PlayTracksAsync(tracks, 0, _source() ?? new QueueSource(QueueSourceKind.Manual, null, "Selected songs"));
    }

    [RelayCommand]
    private void PlayNext()
    {
        if (SelectedTracks() is { Count: > 0 } tracks)
        {
            _actions.PlayNext(tracks);
            Exit();
        }
    }

    [RelayCommand]
    private void AddToQueue()
    {
        if (SelectedTracks() is { Count: > 0 } tracks)
        {
            _actions.AddToQueue(tracks);
            Exit();
        }
    }

    [RelayCommand]
    private async Task AddToPlaylistAsync()
    {
        if (SelectedTracks() is { Count: > 0 } tracks)
        {
            // The selection stays while the dialog is open, so cancelling it keeps the user's picks.
            await _actions.AddToPlaylistAsync(tracks);
        }
    }

    [RelayCommand]
    private Task RemoveAsync() =>
        _remove is not null && CanRemove && SelectedItems is { Count: > 0 } items ? _remove(items) : Task.CompletedTask;

    private List<Track> SelectedTracks() => [.. SelectedItems.Select(i => i.Track).Where(t => t.IsAvailable)];

    private void AfterChange()
    {
        if (IsActive && !_isExplicit && _selected.Count == 0)
        {
            _anchor = null;
            IsActive = false;
        }

        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(IsSignedIn));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // A reload replaces the whole list: whatever was selected is gone.
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset && (IsActive || _selected.Count > 0))
        {
            Exit();
        }
    }
}
