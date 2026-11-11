using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Dialogs.Settings;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.ViewModels;

/// <summary>
/// Settings → Playback: the artists whose songs Hush doesn't add by itself (<see cref="IBlocklist"/>), with a dialog to
/// unblock them. Created with the Settings page. UI thread only.
/// </summary>
public sealed partial class BlockedArtistsSettingsViewModel : ObservableObject
{
    private readonly IBlocklist _blocklist;
    private readonly IUiDispatcher _dispatcher;
    private readonly INotificationService _notifications;

    public BlockedArtistsSettingsViewModel(IBlocklist blocklist, IUiDispatcher dispatcher, INotificationService notifications)
    {
        _blocklist = blocklist;
        _dispatcher = dispatcher;
        _notifications = notifications;
        Count = blocklist.BlockedArtists.Count;
        _ = CountWhenLoadedAsync();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    public partial int Count { get; private set; }

    /// <summary>"None", "1 artist", "4 artists".</summary>
    public string ButtonText => Count switch
    {
        0 => "None",
        1 => "1 artist",
        _ => string.Format(CultureInfo.CurrentCulture, "{0:N0} artists", Count),
    };

    /// <summary>The blocked artists, while the dialog is open.</summary>
    public ObservableCollection<BlockedArtistItem> Artists { get; } = [];

    [ObservableProperty]
    public partial bool IsListEmpty { get; private set; }

    [RelayCommand]
    private async Task ShowAsync()
    {
        try
        {
            await _blocklist.LoadAsync();
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Couldn't read your blocked artists", ex);
            return;
        }

        Artists.Clear();
        foreach (var artist in _blocklist.BlockedArtists)
        {
            Artists.Add(new BlockedArtistItem(artist, UnblockAsync));
        }

        IsListEmpty = Artists.Count == 0;
        Count = Artists.Count;
        await BlockedArtistsDialog.ShowAsync(this);
        Artists.Clear();
    }

    private async Task UnblockAsync(BlockedArtistItem item)
    {
        try
        {
            await _blocklist.UnblockAsync(item.Artist.ToArtistRef());
            Artists.Remove(item);
            IsListEmpty = Artists.Count == 0;
            Count = _blocklist.BlockedArtists.Count;
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Couldn't unblock this artist", ex);
        }
    }

    // The list loads in the background at startup; Settings may open first.
    private async Task CountWhenLoadedAsync()
    {
        try
        {
            await _blocklist.LoadAsync();
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Couldn't read your blocked artists", ex);
            return;
        }

        _dispatcher.Run(() => Count = _blocklist.BlockedArtists.Count);
    }
}

/// <summary>A row of the blocked artists dialog.</summary>
public sealed class BlockedArtistItem
{
    public BlockedArtistItem(BlockedArtist artist, Func<BlockedArtistItem, Task> unblock)
    {
        Artist = artist;
        UnblockCommand = new AsyncRelayCommand(() => unblock(this));
    }

    public BlockedArtist Artist { get; }

    public string Name => Artist.Name;

    /// <summary>"Blocked 3 Nov 2026".</summary>
    public string Subtitle => "Blocked " + Artist.BlockedAt.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public string UnblockName => $"Unblock {Artist.Name}";

    public IAsyncRelayCommand UnblockCommand { get; }
}
