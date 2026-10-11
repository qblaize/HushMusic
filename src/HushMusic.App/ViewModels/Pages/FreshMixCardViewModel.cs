using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>The words, cover and playback of "Fresh for you", shared by the Home card and the mix page.</summary>
internal static class FreshMix
{
    public const string Title = "Fresh for you";

    public const string Description = "Artists you love and songs you haven't heard yet. A new mix every Monday.";

    /// <summary>What the mix plays as (it isn't a playlist on the account).</summary>
    public static QueueSource Source { get; } = new(QueueSourceKind.Manual, null, Title);

    /// <summary>"Updated today", "Updated yesterday", "Updated Monday" (this week), else the date.</summary>
    public static string Updated(DateTimeOffset builtAt, DateTime today)
    {
        var built = builtAt.LocalDateTime.Date;
        var days = (today.Date - built).Days;
        return days switch
        {
            <= 0 => "Updated today",
            1 => "Updated yesterday",
            < 7 => "Updated " + built.ToString("dddd", CultureInfo.CurrentCulture),
            _ => "Updated " + built.ToString("d MMMM", CultureInfo.CurrentCulture),
        };
    }

    public static string Songs(int count) => count == 1 ? "1 song" : $"{count:N0} songs";

    /// <summary>The covers of the first four different albums (or songs without one).</summary>
    public static IReadOnlyList<string> Covers(IReadOnlyList<Track> tracks, int minWidth) =>
    [
        .. tracks
            .DistinctBy(t => t.Album?.BrowseId ?? t.VideoId)
            .Select(t => t.ThumbnailFor(minWidth)?.Url)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(4),
    ];

    public static Task PlayAsync(IMediaItemActions actions, IReadOnlyList<Track> tracks, int startIndex = 0) =>
        actions.PlayTracksAsync(tracks, startIndex, Source);

    /// <summary>Plays the mix shuffled (the queue's own shuffle, so it can be turned off again).</summary>
    public static async Task ShuffleAsync(IMediaItemActions actions, IQueueService queue, IReadOnlyList<Track> tracks)
    {
        if (tracks.Count == 0)
        {
            return;
        }

        await actions.PlayTracksAsync(tracks, Random.Shared.Next(tracks.Count), Source);
        if (queue.Source == Source && !queue.IsShuffled)
        {
            queue.SetShuffle(true);
        }
    }
}

/// <summary>The "Fresh for you" card at the top of Home. Hidden while there is no mix. UI thread only.</summary>
public sealed partial class FreshMixCardViewModel : ObservableObject
{
    private readonly IWeeklyMix _mix;
    private readonly IMediaItemActions _actions;
    private readonly IQueueService _queue;
    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private WeeklyMix? _shown;

    public FreshMixCardViewModel(IWeeklyMix mix, IMediaItemActions actions, IQueueService queue, INavigationService navigation, IUiDispatcher dispatcher)
    {
        _mix = mix;
        _actions = actions;
        _queue = queue;
        _navigation = navigation;
        _dispatcher = dispatcher;

        // Home is created once and kept, so the card follows the mix for the life of the app.
        _mix.Changed += (_, _) => _dispatcher.Run(Refresh);
        _ = LoadAsync();
    }

    public string Title => FreshMix.Title;

    public string Description => FreshMix.Description;

    [ObservableProperty]
    public partial bool IsVisible { get; private set; }

    /// <summary>"Updated Monday · 50 songs".</summary>
    [ObservableProperty]
    public partial string Details { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<string> Covers { get; private set; } = [];

    /// <summary>Shows the current mix; also brings "Updated today" up to date when Home is shown again.</summary>
    public void Refresh()
    {
        var mix = _mix.Current;
        IsVisible = mix is { Tracks.Count: > 0 };
        if (mix is null)
        {
            _shown = null;
            return;
        }

        Details = $"{FreshMix.Updated(mix.BuiltAt, DateTime.Now)} · {FreshMix.Songs(mix.Tracks.Count)}";
        if (!ReferenceEquals(mix, _shown))
        {
            _shown = mix;
            Covers = FreshMix.Covers(mix.Tracks, 120);
        }
    }

    [RelayCommand]
    private Task PlayAsync() => _mix.Current is { } mix ? FreshMix.PlayAsync(_actions, mix.Tracks) : Task.CompletedTask;

    [RelayCommand]
    private Task ShuffleAsync() => _mix.Current is { } mix ? FreshMix.ShuffleAsync(_actions, _queue, mix.Tracks) : Task.CompletedTask;

    [RelayCommand]
    private void Open() => _navigation.NavigateTo(PageKey.FreshMix);

    private async Task LoadAsync()
    {
        try
        {
            await _mix.LoadAsync();
        }
        catch (Exception)
        {
            // Unreadable: the card stays hidden and the next weekly build replaces the file.
        }

        _dispatcher.Run(Refresh);
    }
}
