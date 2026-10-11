using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Dialogs.Settings;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.ViewModels;

/// <summary>
/// Settings → Playback: the smart skip switch and the songs it leaves out (<see cref="ISmartSkip"/>), with a dialog to
/// forgive them. Created with the Settings page. UI thread only.
/// </summary>
public sealed partial class SmartSkipSettingsViewModel : ObservableObject
{
    private readonly ISmartSkip _smartSkip;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly INotificationService _notifications;
    private readonly bool _loading;

    public SmartSkipSettingsViewModel(ISmartSkip smartSkip, ISettingsService settings, IUiDispatcher dispatcher, INotificationService notifications)
    {
        _smartSkip = smartSkip;
        _settings = settings;
        _dispatcher = dispatcher;
        _notifications = notifications;
        _loading = true;
        IsEnabled = settings.Current.SmartSkip;
        _loading = false;
        Count = smartSkip.OftenSkipped.Count;
        _ = CountWhenLoadedAsync();
    }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    public partial int Count { get; private set; }

    /// <summary>"Songs you skip (3)".</summary>
    public string ButtonText => $"Songs you skip ({Count})";

    /// <summary>The often-skipped songs, while the dialog is open.</summary>
    public ObservableCollection<SkippedSongItem> Songs { get; } = [];

    [ObservableProperty]
    public partial bool IsListEmpty { get; private set; }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loading)
        {
            _ = SaveAsync(value);
        }
    }

    [RelayCommand]
    private async Task ShowSongsAsync()
    {
        await _smartSkip.LoadAsync();
        Songs.Clear();
        foreach (var song in _smartSkip.OftenSkipped)
        {
            Songs.Add(new SkippedSongItem(song, ForgiveAsync));
        }

        IsListEmpty = Songs.Count == 0;
        await SkippedSongsDialog.ShowAsync(this);
        Songs.Clear();
    }

    private async Task ForgiveAsync(SkippedSongItem item)
    {
        try
        {
            await _smartSkip.ForgiveAsync(item.Song.VideoId);
            Songs.Remove(item);
            IsListEmpty = Songs.Count == 0;
            Count = _smartSkip.OftenSkipped.Count;
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Couldn't forgive this song", ex);
        }
    }

    private async Task SaveAsync(bool enabled)
    {
        try
        {
            await _settings.UpdateAsync(s => s.SmartSkip = enabled);
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Couldn't save the setting", ex);
        }
    }

    // The skips load in the background at startup; Settings may open first.
    private async Task CountWhenLoadedAsync()
    {
        try
        {
            await _smartSkip.LoadAsync();
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Couldn't read the songs you skip", ex);
            return;
        }

        _dispatcher.Run(() => Count = _smartSkip.OftenSkipped.Count);
    }
}

/// <summary>A row of the skipped songs dialog.</summary>
public sealed class SkippedSongItem
{
    public SkippedSongItem(SkippedSong song, Func<SkippedSongItem, Task> forgive)
    {
        Song = song;
        ForgiveCommand = new AsyncRelayCommand(() => forgive(this));
    }

    public SkippedSong Song { get; }

    public string Title => Song.Title;

    /// <summary>"Artist · skipped 4 times".</summary>
    public string Subtitle => string.IsNullOrWhiteSpace(Song.Artists) ? Skips : $"{Song.Artists} · {Skips}";

    public string? ArtUrl => Song.ArtUrl;

    public string ForgiveName => $"Forgive {Song.Title}";

    public IAsyncRelayCommand ForgiveCommand { get; }

    private string Skips => $"skipped {Song.Skips} times";
}
