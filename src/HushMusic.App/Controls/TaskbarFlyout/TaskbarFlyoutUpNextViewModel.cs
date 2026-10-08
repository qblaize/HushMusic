using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.Controls.TaskbarFlyout;

/// <summary>
/// The flyout's "Up next" row: the next <see cref="MaxItems"/> queue items after the current one, in play order (so it
/// follows shuffle), fewer near the end of the queue. Re-synced from the queue on every change and diffed by
/// <see cref="QueueItem.Id"/>, so covers that stay in the row don't reload. Queue events arrive on any thread and are
/// marshalled to the UI thread.
/// </summary>
public sealed partial class TaskbarFlyoutUpNextViewModel : ObservableObject, IDisposable
{
    public const int MaxItems = 3;

    private readonly IQueueService _queue;
    private readonly IPlayer _player;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;

    public TaskbarFlyoutUpNextViewModel(IQueueService queue, IPlayer player, INotificationService notifications, IUiDispatcher dispatcher)
    {
        _queue = queue;
        _player = player;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _queue.Changed += OnQueueChanged;
        _queue.CurrentChanged += OnCurrentChanged;
        _dispatcher.Run(Sync);
    }

    public ObservableCollection<TaskbarFlyoutUpNextItem> Items { get; } = [];

    /// <summary>False when nothing comes after the current item: the row and its hairline are hidden.</summary>
    [ObservableProperty]
    public partial bool HasItems { get; set; }

    public void Dispose()
    {
        _disposed = true;
        _queue.Changed -= OnQueueChanged;
        _queue.CurrentChanged -= OnCurrentChanged;
    }

    private void OnQueueChanged(object? sender, QueueChangedEventArgs e) => _dispatcher.Run(Sync);

    private void OnCurrentChanged(object? sender, QueueCurrentChangedEventArgs e) => _dispatcher.Run(Sync);

    private async Task PlayAsync(TaskbarFlyoutUpNextItem item)
    {
        var index = IndexInQueue(item.Id);
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

    private int IndexInQueue(Guid id)
    {
        var items = _queue.Items;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    private void Sync()
    {
        if (_disposed)
        {
            return;
        }

        QueueSnapshot snapshot;
        try
        {
            // One consistent read of the items and the cursor.
            snapshot = _queue.GetSnapshot();
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Could not read the queue", ex);
            return;
        }

        var first = snapshot.CurrentIndex + 1;
        var count = snapshot.CurrentIndex < 0 ? 0 : Math.Clamp(snapshot.Items.Count - first, 0, MaxItems);
        for (var i = 0; i < count; i++)
        {
            var queueItem = snapshot.Items[first + i];
            if (i >= Items.Count || Items[i].Id != queueItem.Id)
            {
                var existing = IndexOf(queueItem.Id, start: i + 1);
                if (existing >= 0)
                {
                    Items.Move(existing, i);
                }
                else
                {
                    Items.Insert(i, new TaskbarFlyoutUpNextItem(queueItem, PlayAsync));
                }
            }

            Items[i].Position = i + 1;
        }

        while (Items.Count > count)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        HasItems = count > 0;
    }

    private int IndexOf(Guid id, int start)
    {
        for (var i = start; i < Items.Count; i++)
        {
            if (Items[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>One tile of the "Up next" row: a song (cover, title, artists) or a live station (logo, name, description).</summary>
public sealed partial class TaskbarFlyoutUpNextItem : ObservableObject
{
    // Tiles are about 100 px wide; this source stays sharp up to 200 % display scaling.
    private const int CoverSourceWidth = 200;

    public TaskbarFlyoutUpNextItem(QueueItem item, Func<TaskbarFlyoutUpNextItem, Task> play)
    {
        ArgumentNullException.ThrowIfNull(item);
        var track = item.Track;
        Id = item.Id;
        Title = track.Title;
        Subtitle = track.ArtistsText;
        IsStation = track.IsLiveRadio;
        CoverUrl = IsStation ? null : track.ThumbnailFor(CoverSourceWidth)?.Url;
        StationLogoUrl = track.Station?.LogoUrl;
        StationName = track.Station?.Name;
        AccessibleName = IsStation || string.IsNullOrWhiteSpace(Subtitle) ? $"Play {Title}" : $"Play {Title} by {Subtitle}";

        // Titles are cut short in a 100 px tile; the tooltip has the full text and the album.
        var album = IsStation ? null : track.Album?.Name;
        var details = string.Join(" · ", new[] { Subtitle, album }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.Ordinal));
        ToolTip = details.Length == 0 ? Title : $"{Title}\n{details}";
        PlayCommand = new AsyncRelayCommand(() => play(this));
    }

    public Guid Id { get; }

    public string Title { get; }

    /// <summary>Artists for a song; genres and country for a station.</summary>
    public string Subtitle { get; }

    public string? CoverUrl { get; }

    /// <summary>A live radio station: its art is drawn from the logo (or initials).</summary>
    public bool IsStation { get; }

    public string? StationLogoUrl { get; }

    public string? StationName { get; }

    /// <summary>"Play Title by Artist".</summary>
    public string AccessibleName { get; }

    public string ToolTip { get; }

    public IAsyncRelayCommand PlayCommand { get; }

    /// <summary>1-based place in the row.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutomationId))]
    public partial int Position { get; set; }

    /// <summary>FlyoutNext1 … FlyoutNext3.</summary>
    public string AutomationId => $"FlyoutNext{Position}";
}
