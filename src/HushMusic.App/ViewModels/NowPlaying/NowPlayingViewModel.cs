using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Services.Shell;
using HushMusic.App.ViewModels.Shell;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.NowPlaying;

public enum NowPlayingTab
{
    UpNext,
    Lyrics,
    Related,
}

/// <summary>
/// The full-window Now Playing view: open state, the right panel's tab, artwork and credits. Transport, seek and volume
/// come from <see cref="PlayerViewModel"/>, shared with the player bar. Any navigation closes the view. Singleton.
/// </summary>
public sealed partial class NowPlayingViewModel : ObservableObject
{
    private readonly INavigationService _navigation;
    private readonly IWindowModeService _windowMode;
    private readonly ISettingsService _settings;

    public NowPlayingViewModel(
        PlayerViewModel player,
        QueuePanelViewModel queue,
        LyricsViewModel lyrics,
        RelatedViewModel related,
        SleepTimerViewModel sleepTimer,
        CoverFlowViewModel coverFlow,
        INavigationService navigation,
        IWindowModeService windowMode,
        ISettingsService settings)
    {
        Player = player;
        Queue = queue;
        Lyrics = lyrics;
        Related = related;
        SleepTimer = sleepTimer;
        CoverFlow = coverFlow;
        _navigation = navigation;
        _windowMode = windowMode;
        _settings = settings;
        IsPanelHidden = settings.Current.NowPlayingPanelHidden;

        Player.PropertyChanged += OnPlayerPropertyChanged;
        CoverFlow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CoverFlowViewModel.IsEnabled))
            {
                UpdateTrack();
                OnPropertyChanged(nameof(ShowsPanel));
                OnPropertyChanged(nameof(CanTogglePanel));
                UpdateActiveTabs();
            }
        };
        Related.Navigating += (_, _) => Close();
        _navigation.Navigated += (_, _) => Close();
        UpdateTrack();
    }

    public PlayerViewModel Player { get; }

    public QueuePanelViewModel Queue { get; }

    public LyricsViewModel Lyrics { get; }

    public RelatedViewModel Related { get; }

    public SleepTimerViewModel SleepTimer { get; }

    /// <summary>The covers before and after the current one; off (the single artwork shows) when the setting is Single.</summary>
    public CoverFlowViewModel CoverFlow { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpNextActive), nameof(IsLyricsActive))]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpNextTab), nameof(IsLyricsTab), nameof(IsRelatedTab), nameof(IsUpNextActive), nameof(IsLyricsActive))]
    public partial NowPlayingTab Tab { get; set; }

    /// <summary>
    /// Hi-res art for the single artwork (resized by the image host); may fail, so the view falls back to
    /// <see cref="HeroArtFallbackUrl"/>. Null while the Cover Flow shows instead (its covers load their own art).
    /// </summary>
    [ObservableProperty]
    public partial string? HeroArtUrl { get; set; }

    [ObservableProperty]
    public partial string? HeroArtFallbackUrl { get; set; }

    /// <summary>Small art for the blurred, drifting background.</summary>
    [ObservableProperty]
    public partial string? BackdropArtUrl { get; set; }

    /// <summary>Artists, then the album: links where they have a page.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CreditLink> Credits { get; set; } = [];

    /// <summary>Artists only (player bar).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CreditLink> ArtistCredits { get; set; } = [];

    /// <summary>Cover Flow mode lets the user hide the side panel; the single artwork always shows it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsPanel), nameof(PanelToggleLabel))]
    public partial bool IsPanelHidden { get; set; }

    public string PanelToggleLabel => IsPanelHidden ? "Show Up next and lyrics" : "Hide Up next and lyrics";

    public bool ShowsPanel => !CoverFlow.IsEnabled || !IsPanelHidden;

    public bool CanTogglePanel => CoverFlow.IsEnabled;

    /// <summary>Lyrics and Related exist for YouTube tracks only, not live radio.</summary>
    public bool HasSongTabs => !Player.IsLive;

    /// <summary>The player bar's lyrics button.</summary>
    public bool CanShowLyrics => Player.HasTrack && !Player.IsLive;

    public bool IsUpNextTab => Tab == NowPlayingTab.UpNext;

    public bool IsLyricsTab => Tab == NowPlayingTab.Lyrics;

    public bool IsRelatedTab => Tab == NowPlayingTab.Related;

    /// <summary>Player bar toggles: checked while Now Playing shows that tab.</summary>
    public bool IsUpNextActive => IsOpen && IsUpNextTab;

    public bool IsLyricsActive => IsOpen && IsLyricsTab;

    /// <summary>Opens the view (on <paramref name="tab"/> when given). Does nothing while nothing is playing.</summary>
    public void Open(NowPlayingTab? tab = null)
    {
        if (!Player.HasTrack)
        {
            return;
        }

        if (tab is { } value)
        {
            Tab = value;

            // Asked for a tab (player bar lyrics / queue buttons): show it even if the panel was hidden.
            if (IsPanelHidden)
            {
                TogglePanel();
            }
        }

        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    /// <summary>Player bar artwork / title, and the big artwork / title inside the view.</summary>
    [RelayCommand]
    private void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    [RelayCommand]
    private void ToggleUpNext() => ToggleTab(NowPlayingTab.UpNext);

    [RelayCommand]
    private void ToggleLyrics() => ToggleTab(NowPlayingTab.Lyrics);

    /// <summary>Segmented control inside the view: switch tabs without closing.</summary>
    [RelayCommand]
    private void ShowUpNext() => ShowTab(NowPlayingTab.UpNext);

    [RelayCommand]
    private void ShowLyrics() => ShowTab(NowPlayingTab.Lyrics);

    [RelayCommand]
    private void ShowRelated() => ShowTab(NowPlayingTab.Related);

    [RelayCommand]
    private void EnterMiniPlayer()
    {
        Close();
        _windowMode.EnterMiniPlayer();
    }

    [RelayCommand]
    private void TogglePanel()
    {
        IsPanelHidden = !IsPanelHidden;
        var hidden = IsPanelHidden;
        _ = _settings.UpdateAsync(s => s.NowPlayingPanelHidden = hidden);
    }

    partial void OnIsPanelHiddenChanged(bool value) => UpdateActiveTabs();

    partial void OnIsOpenChanged(bool value) => UpdateActiveTabs();

    partial void OnTabChanged(NowPlayingTab value) => UpdateActiveTabs();

    private void UpdateActiveTabs()
    {
        // A hidden panel stops lyrics following the song and related loading.
        var panel = IsOpen && ShowsPanel;
        Lyrics.SetActive(panel && Tab == NowPlayingTab.Lyrics);
        Related.SetActive(panel && Tab == NowPlayingTab.Related);
    }

    private void ToggleTab(NowPlayingTab tab)
    {
        if (tab != NowPlayingTab.UpNext && !HasSongTabs)
        {
            return;
        }

        if (IsOpen && Tab == tab)
        {
            Close();
            return;
        }

        Open(tab);
    }

    private void ShowTab(NowPlayingTab tab)
    {
        if (tab != NowPlayingTab.UpNext && !HasSongTabs)
        {
            tab = NowPlayingTab.UpNext;
        }

        Tab = tab;

        // The segments are toggle buttons: re-assert all three, also when the tab didn't change (a click unchecks it locally).
        OnPropertyChanged(nameof(IsUpNextTab));
        OnPropertyChanged(nameof(IsLyricsTab));
        OnPropertyChanged(nameof(IsRelatedTab));
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.Track))
        {
            UpdateTrack();
        }
        else if (e.PropertyName == nameof(PlayerViewModel.Subtitle) && Player.IsLive)
        {
            UpdateLiveCredits();
        }
    }

    private void UpdateTrack()
    {
        var track = Player.Track;

        OnPropertyChanged(nameof(HasSongTabs));
        OnPropertyChanged(nameof(CanShowLyrics));
        if (track?.IsLiveRadio == true)
        {
            // A station: its artwork is drawn by StationArt (logos come in every size); the logo also colours the backdrop.
            HeroArtFallbackUrl = null;
            HeroArtUrl = null;
            BackdropArtUrl = NowPlayingArt.Small(track);
            UpdateLiveCredits();
            if (Tab != NowPlayingTab.UpNext)
            {
                ShowTab(NowPlayingTab.UpNext);
            }

            return;
        }

        // Fallback first: the artwork reloads when the main URL changes and reads the fallback then.
        var single = !CoverFlow.IsEnabled;
        HeroArtFallbackUrl = single ? NowPlayingArt.Listed(track) : null;
        HeroArtUrl = single ? NowPlayingArt.Large(track, NowPlayingArt.HeroPixels) : null;
        BackdropArtUrl = NowPlayingArt.Small(track);
        Credits = track is null ? [] : BuildCredits(track, withAlbum: true);
        ArtistCredits = track is null ? [] : BuildCredits(track, withAlbum: false);
        if (track is null)
        {
            Close();
        }
    }

    // Live radio: one plain line, "Artist · Station" (or the station's description), following the ICY title.
    private void UpdateLiveCredits()
    {
        List<CreditLink> line = [new CreditLink(Player.Subtitle, null, null)];
        Credits = line;
        ArtistCredits = line;
    }

    private List<CreditLink> BuildCredits(Track track, bool withAlbum)
    {
        var credits = new List<CreditLink>();
        foreach (var artist in track.Artists)
        {
            var channelId = artist.BrowseId;
            credits.Add(new CreditLink(
                artist.Name,
                credits.Count == 0 ? null : ", ",
                string.IsNullOrWhiteSpace(channelId) ? null : () => NavigateTo(PageKey.Artist, channelId)));
        }

        if (withAlbum && track.Album is { Name.Length: > 0 } album)
        {
            var browseId = album.BrowseId;
            credits.Add(new CreditLink(
                album.Name,
                credits.Count == 0 ? null : " — ",
                string.IsNullOrWhiteSpace(browseId) ? null : () => NavigateTo(PageKey.Album, browseId)));
        }

        return credits;
    }

    // Close first: navigating to the page that is already showing raises no Navigated event.
    private void NavigateTo(PageKey page, string id)
    {
        Close();
        _navigation.NavigateTo(page, id);
    }
}

/// <summary>One credit under the title; <see cref="Open"/> is null for plain text.</summary>
public sealed record CreditLink(string Text, string? Separator, Action? Open);
