using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>The "Fresh for you" page: the weekly mix's songs, Play, Shuffle and "Save as playlist" (signed in).</summary>
public sealed partial class FreshMixViewModel : PageViewModelBase, ITrackListHost, ITrackSelectionHost
{
    private readonly IWeeklyMix _mix;
    private readonly IQueueService _queue;
    private readonly IAccountActionsService _account;
    private WeeklyMix? _shown;

    public FreshMixViewModel(IWeeklyMix mix, IQueueService queue, IAccountActionsService account, PageServices services)
        : base(services)
    {
        _mix = mix;
        _queue = queue;
        _account = account;
        Selection = new TrackSelection(Tracks, services.Actions, () => FreshMix.Source);
    }

    public string Title => FreshMix.Title;

    public string Description => FreshMix.Description;

    public ObservableCollection<TrackItem> Tracks { get; } = [];

    public TrackSelection Selection { get; }

    /// <summary>"Updated Monday · 50 songs, 3 h 5 min".</summary>
    [ObservableProperty]
    public partial string Details { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<string> Covers { get; private set; } = [];

    /// <summary>The large cover's first album, for the artwork wash behind the header.</summary>
    [ObservableProperty]
    public partial string? WashUrl { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAsPlaylistCommand))]
    public partial bool IsSignedIn { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAsPlaylistCommand))]
    public partial bool IsSaving { get; private set; }

    public Task PlayFromTrackAsync(Track track)
    {
        var tracks = Tracks.Select(t => t.Track).ToList();
        var index = tracks.FindIndex(t => ReferenceEquals(t, track));
        return index < 0 ? Task.CompletedTask : FreshMix.PlayAsync(Actions, tracks, index);
    }

    public bool CanRemoveFromPlaylist(Track track) => false;

    public Task RemoveFromPlaylistAsync(Track track) => Task.CompletedTask;

    protected override async Task OnNavigatedToCoreAsync(object? parameter)
    {
        _mix.Changed += OnMixChanged;
        Services.Auth.StatusChanged += OnAuthStatusChanged;
        IsSignedIn = Actions.IsSignedIn;
        await LoadAsync();
    }

    protected override void OnNavigatedFromCore()
    {
        _mix.Changed -= OnMixChanged;
        Services.Auth.StatusChanged -= OnAuthStatusChanged;
        Selection.Exit();
    }

    protected override Task LoadAsync() => RunAsync(
        async ct =>
        {
            await _mix.LoadAsync(ct);
            Show(_mix.Current);
        },
        "Couldn't load your mix");

    [RelayCommand]
    private Task PlayAsync() => FreshMix.PlayAsync(Actions, [.. Tracks.Select(t => t.Track)]);

    [RelayCommand]
    private Task ShuffleAsync() => FreshMix.ShuffleAsync(Actions, _queue, [.. Tracks.Select(t => t.Track)]);

    [RelayCommand]
    private Task PlayTrackAsync(TrackItem? item) => item is null ? Task.CompletedTask : PlayFromTrackAsync(item.Track);

    private bool CanSaveAsPlaylist() => IsSignedIn && !IsSaving;

    /// <summary>Creates a private playlist on the account with the mix's songs, in this order.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveAsPlaylist))]
    private async Task SaveAsPlaylistAsync()
    {
        if (_shown is not { } mix || Tracks.Count == 0)
        {
            return;
        }

        var name = $"{FreshMix.Title} · {mix.BuiltAt.LocalDateTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";
        List<string> videoIds = [.. Tracks.Select(t => t.Track.VideoId).Distinct(StringComparer.Ordinal)];
        IsSaving = true;
        try
        {
            var playlistId = await _account.CreatePlaylistAsync(name, FreshMix.Description, PrivacyStatus.Private, videoIds);
            var details = $"“{name}”, {FreshMix.Songs(videoIds.Count)}, private";
            Notifications.Show(new AppNotification(NotificationSeverity.Success, "Mix saved as a playlist", details)
            {
                Action = new NotificationAction("Open", () => Actions.OpenPlaylist(playlistId)),
            });
        }
        catch (Exception ex)
        {
            Notifications.ShowError("Couldn't save the mix as a playlist", ex);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void Show(WeeklyMix? mix)
    {
        if (mix is null || mix.Tracks.Count == 0)
        {
            _shown = null;
            Selection.Exit();
            Tracks.Clear();
            HasContent = false;
            IsEmpty = true;
            return;
        }

        var total = TimeSpan.FromSeconds(mix.Tracks.Sum(t => t.Duration?.TotalSeconds ?? 0));
        Details = $"{FreshMix.Updated(mix.BuiltAt, DateTime.Now)} · {FreshMix.Songs(mix.Tracks.Count)}, {Length(total)}";
        if (!ReferenceEquals(mix, _shown))
        {
            _shown = mix;
            Selection.Exit();
            Tracks.Clear();
            foreach (var item in TrackItem.From(mix.Tracks))
            {
                Tracks.Add(item);
            }

            Covers = FreshMix.Covers(mix.Tracks, 226);
            WashUrl = mix.Tracks[0].ThumbnailFor(60)?.Url;
            Services.Warmup.Warm(mix.Tracks[0]);
        }

        HasContent = true;
        IsEmpty = false;
    }

    // "3 h 5 min" or "48 min", like Your stats.
    private static string Length(TimeSpan total) =>
        total.TotalHours >= 1 ? $"{(int)total.TotalHours} h {total.Minutes} min" : $"{Math.Max(1, (int)Math.Round(total.TotalMinutes))} min";

    private void OnMixChanged(object? sender, EventArgs e) => Services.Dispatcher.Run(() => Show(_mix.Current));

    private void OnAuthStatusChanged(object? sender, AuthStatusChangedEventArgs e) =>
        Services.Dispatcher.Run(() => IsSignedIn = Actions.IsSignedIn);
}
