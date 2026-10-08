using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using HushMusic.App.ViewModels.Shell;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.NowPlaying;

/// <summary>
/// The Cover Flow in Now Playing: the queue items around the current one, in play order (so shuffled when the queue
/// is), keyed by <see cref="QueueItem.Id"/> so the view can glide each cover from its old place to its new one. Only
/// <see cref="Reach"/> items on each side are mirrored. Off, with nothing mirrored, while the "Now Playing artwork"
/// setting is Single. Singleton; every change is applied on the UI thread.
/// </summary>
public sealed partial class CoverFlowViewModel : ObservableObject
{
    public const string CoverFlowStyle = "CoverFlow";
    public const string SingleStyle = "Single";

    /// <summary>The most covers shown on each side of the current one.</summary>
    public const int Reach = 3;

    private readonly IQueueService _queue;
    private readonly IPlayer _player;
    private readonly PlayerViewModel _playerViewModel;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<CoverFlowViewModel> _logger;
    private Dictionary<Guid, int> _offsets = [];
    private Dictionary<Guid, int> _previousOffsets = [];
    private int _syncQueued;

    public CoverFlowViewModel(
        IQueueService queue,
        IPlayer player,
        PlayerViewModel playerViewModel,
        ISettingsService settings,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        ILogger<CoverFlowViewModel> logger)
    {
        _queue = queue;
        _player = player;
        _playerViewModel = playerViewModel;
        _settings = settings;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _logger = logger;

        _queue.Changed += (_, _) => RequestSync();
        _queue.CurrentChanged += (_, _) => RequestSync();
        _settings.Changed += (_, _) => _dispatcher.Run(ApplySetting);
        _playerViewModel.PropertyChanged += OnPlayerPropertyChanged;
        _dispatcher.Run(ApplySetting);
    }

    /// <summary>False while the setting is Single: <see cref="Items"/> is empty and the view shows the single cover.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>The current item (offset 0) and up to <see cref="Reach"/> items on each side, by offset.</summary>
    public IReadOnlyList<CoverFlowItem> Items { get; private set; } = [];

    /// <summary><see cref="Items"/> changed (UI thread).</summary>
    public event EventHandler? ItemsChanged;

    public static bool IsCoverFlow(string? style) => !string.Equals(style, SingleStyle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where a queue item is now, relative to the current one; null when it is no longer queued.</summary>
    public int? OffsetOf(Guid id) => _offsets.TryGetValue(id, out var offset) ? offset : null;

    /// <summary>Where a queue item was before the last change; null when it wasn't queued.</summary>
    public int? PreviousOffsetOf(Guid id) => _previousOffsets.TryGetValue(id, out var offset) ? offset : null;

    private void ApplySetting()
    {
        var enabled = IsCoverFlow(_settings.Current.NowPlayingArtStyle);
        if (enabled != IsEnabled)
        {
            IsEnabled = enabled;
            Sync();
        }
    }

    // Queue events come from any thread, often several at once (a new queue raises Changed and CurrentChanged):
    // one sync on the UI thread reads the final state.
    private void RequestSync()
    {
        if (Interlocked.Exchange(ref _syncQueued, 1) == 0)
        {
            _dispatcher.Run(() =>
            {
                Volatile.Write(ref _syncQueued, 0);
                Sync();
            });
        }
    }

    private void Sync()
    {
        if (!IsEnabled)
        {
            _offsets = [];
            _previousOffsets = [];
            if (Items.Count > 0)
            {
                Items = [];
                ItemsChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        QueueSnapshot snapshot;
        try
        {
            snapshot = _queue.GetSnapshot();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the queue for the Cover Flow");
            return;
        }

        var queued = snapshot.Items;
        var current = snapshot.CurrentIndex;
        var offsets = new Dictionary<Guid, int>(queued.Count);
        var reused = Items.ToDictionary(i => i.Id);
        List<CoverFlowItem> window = [];
        if (current >= 0 && current < queued.Count)
        {
            for (var i = 0; i < queued.Count; i++)
            {
                offsets.TryAdd(queued[i].Id, i - current);
            }

            for (var i = Math.Max(0, current - Reach); i <= Math.Min(queued.Count - 1, current + Reach); i++)
            {
                window.Add(Reuse(queued[i], i - current));
            }
        }
        else if (_playerViewModel.Track is { } track)
        {
            // Not expected (the player plays the queue's current item), but never leave Now Playing without its cover.
            window.Add(Reuse(new QueueItem(Guid.Empty, track), 0));
        }

        _previousOffsets = _offsets;
        _offsets = offsets;
        Items = window;
        UpdateLiveArt();
        ItemsChanged?.Invoke(this, EventArgs.Empty);

        CoverFlowItem Reuse(QueueItem queueItem, int offset)
        {
            var item = reused.TryGetValue(queueItem.Id, out var existing) && ReferenceEquals(existing.Track, queueItem.Track)
                ? existing
                : new CoverFlowItem(queueItem, PlayAsync);
            item.Offset = offset;
            return item;
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.LiveArtUrl) or nameof(PlayerViewModel.Track))
        {
            UpdateLiveArt();
        }
    }

    // A playing station shows the song's cover when it sends one (as the single artwork does); the others their logo.
    private void UpdateLiveArt()
    {
        var playing = _playerViewModel.Track?.Station?.Id;
        foreach (var item in Items)
        {
            if (item.Track.Station is { } station)
            {
                item.LogoUrl = item.Offset == 0 && string.Equals(station.Id, playing, StringComparison.Ordinal)
                    ? _playerViewModel.LiveArtUrl ?? station.LogoUrl
                    : station.LogoUrl;
            }
        }
    }

    private async Task PlayAsync(CoverFlowItem item)
    {
        var items = _queue.Items;
        var index = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == item.Id)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        try
        {
            await _player.PlayQueueIndexAsync(index);
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Could not play {item.Title}", ex);
        }
    }
}

/// <summary>One cover of the Cover Flow.</summary>
public sealed partial class CoverFlowItem : ObservableObject
{
    public CoverFlowItem(QueueItem item, Func<CoverFlowItem, Task> play)
    {
        Id = item.Id;
        var track = item.Track;
        Track = track;
        Title = track.Title;
        Artists = track.ArtistsText;
        IsStation = track.IsLiveRadio;
        StationName = track.Station?.Name;
        LogoUrl = track.Station?.LogoUrl;

        // Every cover asks for the hero size: the next one is already cached when it moves to the middle.
        ArtUrl = IsStation ? null : NowPlayingArt.Large(track, NowPlayingArt.HeroPixels);
        ArtFallbackUrl = IsStation ? null : NowPlayingArt.Listed(track);
        PlayCommand = new AsyncRelayCommand(() => play(this));
    }

    public Guid Id { get; }

    public Track Track { get; }

    public string Title { get; }

    public string Artists { get; }

    /// <summary>Relative to the current item: negative before it, positive after it.</summary>
    public int Offset { get; internal set; }

    public string? ArtUrl { get; }

    public string? ArtFallbackUrl { get; }

    /// <summary>A live radio station: its art is drawn from <see cref="LogoUrl"/> (or initials).</summary>
    public bool IsStation { get; }

    public string? StationName { get; }

    [ObservableProperty]
    public partial string? LogoUrl { get; set; }

    /// <summary>Accessible name of a side cover.</summary>
    public string PlayLabel => IsStation || string.IsNullOrWhiteSpace(Artists) ? $"Play {Title}" : $"Play {Title} by {Artists}";

    public string ToolTip => string.IsNullOrWhiteSpace(Artists) ? Title : $"{Title} — {Artists}";

    public IAsyncRelayCommand PlayCommand { get; }
}
