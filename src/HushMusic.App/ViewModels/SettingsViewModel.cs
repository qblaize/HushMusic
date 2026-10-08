using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;
using Serilog.Core;
using Serilog.Events;
using Windows.UI;
using HushMusic.App.Controls.Settings;
using HushMusic.App.Services.Shell;
using HushMusic.App.Services.Updates;
using HushMusic.App.ViewModels.NowPlaying;
using HushMusic.App.ViewModels.Shell;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase, INavigationAware
{
    private static readonly Uri LastFmApiAccountPage = new("https://www.last.fm/api/account/create");

    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly IStreamResolver _streamResolver;
    private readonly LoggingLevelSwitch _levelSwitch;
    private readonly IThemeService _theme;
    private readonly ILastFmService _lastFm;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUpdateService _appUpdates;
    private CancellationTokenSource? _updateCts;
    private CancellationTokenSource? _lastFmCts;
    private Uri? _lastFmApprovalPage;
    private bool _loading;
    private bool _subscribed;

    public SettingsViewModel(
        ISettingsService settings,
        IAppPaths paths,
        IStreamResolver streamResolver,
        AccountViewModel account,
        LoggingLevelSwitch levelSwitch,
        IThemeService theme,
        ILastFmService lastFm,
        IUiDispatcher dispatcher,
        IUpdateService appUpdates,
        INotificationService notifications)
        : base(notifications)
    {
        _settings = settings;
        _paths = paths;
        _streamResolver = streamResolver;
        _levelSwitch = levelSwitch;
        _theme = theme;
        _lastFm = lastFm;
        _dispatcher = dispatcher;
        _appUpdates = appUpdates;
        Account = account;
        AccentSwatches = BuildAccentSwatches();
    }

    public AccountViewModel Account { get; }

    public IReadOnlyList<string> LogLevels { get; } = ["Verbose", "Debug", "Information", "Warning", "Error"];

    public IReadOnlyList<SettingsChoice> ThemeOptions { get; } =
    [
        new("Light", "Light"),
        new("Dark", "Dark"),
        new("System", "Auto"),
    ];

    public IReadOnlyList<SettingsChoice> DesignSystemOptions { get; } =
    [
        new(DesignSystems.Hush, "Hush"),
        new(DesignSystems.Windows, "Windows"),
    ];

    public IReadOnlyList<SettingsChoice> PlayerLayoutOptions { get; } =
    [
        new(PlayerLayouts.Standard, "Standard"),
        new(PlayerLayouts.Minimal, "Minimal"),
    ];

    public IReadOnlyList<SettingsChoice> NowPlayingArtStyleOptions { get; } =
    [
        new(CoverFlowViewModel.SingleStyle, "Single"),
        new(CoverFlowViewModel.CoverFlowStyle, "Cover Flow"),
    ];

    public string LogFolder => _paths.Logs;

    public string AppVersion => $"Version {_appUpdates.CurrentVersion}";

    // ===== Appearance =====

    [ObservableProperty]
    public partial string Theme { get; set; } = "Dark";

    [ObservableProperty]
    public partial string AccentStyle { get; set; } = AccentPresets.Artwork;

    [ObservableProperty]
    public partial string NowPlayingArtStyle { get; set; } = CoverFlowViewModel.CoverFlowStyle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDesignRestartPending))]
    public partial string DesignSystem { get; set; } = DesignSystems.Hush;

    /// <summary>The chosen design differs from the one the app started with.</summary>
    public bool IsDesignRestartPending => DesignSystem != DesignSystems.Active;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArtStyleAvailable), nameof(ArtStyleDescription))]
    public partial string PlayerLayout { get; set; } = PlayerLayouts.Standard;

    /// <summary>The Minimal layout always shows the single cover.</summary>
    public bool IsArtStyleAvailable => !PlayerLayouts.IsMinimal(PlayerLayout);

    public string ArtStyleDescription => IsArtStyleAvailable
        ? "Show just the current cover, or the songs before and after it as a Cover Flow."
        : "The Minimal player layout always shows just the current cover.";

    /// <summary>"Album art" first, then the Windows accent and the presets, coloured for the theme on screen.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<AccentSwatch> AccentSwatches { get; set; } = [];

    // ===== Playback / window =====

    [ObservableProperty]
    public partial bool ReportPlaybackHistory { get; set; }

    [ObservableProperty]
    public partial bool UseAccountForStreams { get; set; }

    [ObservableProperty]
    public partial bool NormalizeVolume { get; set; }

    [ObservableProperty]
    public partial bool ResumeLastSession { get; set; }

    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial bool ShowTaskbarWidget { get; set; }

    // ===== Content / stream resolver / diagnostics =====

    [ObservableProperty]
    public partial bool CheckYtDlpUpdatesOnStartup { get; set; }

    [ObservableProperty]
    public partial string? YtDlpPath { get; set; }

    [ObservableProperty]
    public partial string YtDlpJsRuntime { get; set; } = "auto";

    [ObservableProperty]
    public partial string ContentLocation { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LogLevel { get; set; } = "Information";

    [ObservableProperty]
    public partial string YtDlpVersion { get; set; } = "Checking…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheckForUpdate))]
    public partial bool IsCheckingForUpdate { get; set; }

    [ObservableProperty]
    public partial bool IsUpdateResultOpen { get; set; }

    [ObservableProperty]
    public partial string UpdateResultTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpdateResultMessage { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateResultNotificationSeverity), nameof(UpdateResultGlyph))]
    public partial InfoBarSeverity UpdateResultSeverity { get; set; }

    /// <summary>Colour of the inline update result (the shell maps it to a brush).</summary>
    public NotificationSeverity UpdateResultNotificationSeverity => UpdateResultSeverity switch
    {
        InfoBarSeverity.Error => NotificationSeverity.Error,
        InfoBarSeverity.Warning => NotificationSeverity.Warning,
        InfoBarSeverity.Success => NotificationSeverity.Success,
        _ => NotificationSeverity.Informational,
    };

    public string UpdateResultGlyph => UpdateResultSeverity switch
    {
        InfoBarSeverity.Error => "",
        InfoBarSeverity.Warning => "",
        InfoBarSeverity.Success => "",
        _ => "",
    };

    public bool CanCheckForUpdate => !IsCheckingForUpdate;

    // ===== About: app updates =====

    [ObservableProperty]
    public partial string AppUpdateStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsAppUpdateBusy { get; set; }

    [ObservableProperty]
    public partial bool IsAppUpdateReady { get; set; }

    /// <summary>"Check for updates" is shown in installed builds that have an update source, until an update is ready.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForAppUpdateCommand))]
    public partial bool ShowCheckForAppUpdate { get; set; }

    // ===== Last.fm =====

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsLastFmDisconnected),
        nameof(IsLastFmAwaitingApproval),
        nameof(IsLastFmConnected),
        nameof(LastFmStatusTitle),
        nameof(LastFmStatusDetail))]
    public partial LastFmState LastFmState { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastFmStatusTitle))]
    public partial string? LastFmUserName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastFmStatusDetail))]
    public partial int LastFmPendingScrobbles { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectLastFmCommand))]
    public partial string LastFmApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectLastFmCommand))]
    public partial string LastFmSharedSecret { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool LastFmScrobbling { get; set; }

    /// <summary>A short inline note under the Last.fm status, e.g. "not approved yet".</summary>
    [ObservableProperty]
    public partial string? LastFmHint { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(
        nameof(ConnectLastFmCommand),
        nameof(CompleteLastFmConnectCommand),
        nameof(CancelLastFmConnectCommand),
        nameof(DisconnectLastFmCommand))]
    public partial bool IsLastFmWorking { get; set; }

    public bool IsLastFmDisconnected => LastFmState == LastFmState.Disconnected;

    public bool IsLastFmAwaitingApproval => LastFmState == LastFmState.AwaitingApproval;

    public bool IsLastFmConnected => LastFmState == LastFmState.Connected;

    public string LastFmStatusTitle => LastFmState switch
    {
        LastFmState.Connected => $"Connected as {LastFmUserName}",
        LastFmState.AwaitingApproval => "Waiting for you to approve in the browser…",
        _ => "Not connected",
    };

    public string LastFmStatusDetail => LastFmState switch
    {
        LastFmState.Connected when LastFmPendingScrobbles == 1 => "1 scrobble is waiting to be sent.",
        LastFmState.Connected when LastFmPendingScrobbles > 1 => $"{LastFmPendingScrobbles:N0} scrobbles are waiting to be sent.",
        LastFmState.Connected => "Songs you listen to are added to your Last.fm profile.",
        LastFmState.AwaitingApproval => "Select \"Yes, allow access\" on last.fm, then come back here.",
        _ => "Add the songs you play here to your Last.fm profile.",
    };

    public async Task OnNavigatedToAsync(object? parameter)
    {
        _loading = true;
        var current = _settings.Current;
        Theme = current.Theme;
        AccentStyle = AccentPresets.Normalize(current.AccentStyle);
        NowPlayingArtStyle = CoverFlowViewModel.IsCoverFlow(current.NowPlayingArtStyle) ? CoverFlowViewModel.CoverFlowStyle : CoverFlowViewModel.SingleStyle;
        DesignSystem = DesignSystems.Normalize(current.DesignSystem);
        PlayerLayout = PlayerLayouts.IsMinimal(current.PlayerLayout) ? PlayerLayouts.Minimal : PlayerLayouts.Standard;
        ReportPlaybackHistory = current.ReportPlaybackHistory;
        UseAccountForStreams = current.UseAccountForStreams;
        NormalizeVolume = current.NormalizeVolume;
        ResumeLastSession = current.ResumeLastSession;
        CloseToTray = current.CloseToTray;
        StartWithWindows = current.StartWithWindows;
        ShowTaskbarWidget = current.ShowTaskbarWidget;
        LastFmScrobbling = current.LastFmScrobbling;
        CheckYtDlpUpdatesOnStartup = current.CheckYtDlpUpdatesOnStartup;
        YtDlpPath = current.YtDlpPath;
        YtDlpJsRuntime = current.YtDlpJsRuntime;
        ContentLocation = current.ContentLocation;
        LogLevel = current.LogLevel;
        _loading = false;

        if (!_subscribed)
        {
            _subscribed = true;
            _theme.ThemeChanged += OnAppThemeChanged;
            SystemAccent.Changed += OnSystemAccentChanged;
            _lastFm.StateChanged += OnLastFmStateChanged;
            _settings.Changed += OnSettingsChanged;
            _appUpdates.StateChanged += OnAppUpdateStateChanged;
        }

        AccentSwatches = BuildAccentSwatches();
        RefreshLastFm();
        RefreshAppUpdate();

        await RunAsync(
            async ct => YtDlpVersion = await _streamResolver.GetBackendVersionAsync(ct) ?? "Not installed",
            "Could not read the yt-dlp version");
    }

    public void OnNavigatedFrom()
    {
        if (_subscribed)
        {
            _subscribed = false;
            _theme.ThemeChanged -= OnAppThemeChanged;
            SystemAccent.Changed -= OnSystemAccentChanged;
            _lastFm.StateChanged -= OnLastFmStateChanged;
            _settings.Changed -= OnSettingsChanged;
            _appUpdates.StateChanged -= OnAppUpdateStateChanged;
        }

        CancelPendingWork();
        _updateCts?.Cancel();
        _lastFmCts?.Cancel();
    }

    [RelayCommand]
    private void DismissUpdateResult() => IsUpdateResultOpen = false;

    [RelayCommand]
    private async Task OpenLogFolderAsync() =>
        await Windows.System.Launcher.LaunchFolderPathAsync(_paths.Logs);

    [RelayCommand]
    private async Task CheckForYtDlpUpdateAsync()
    {
        if (IsCheckingForUpdate)
        {
            return;
        }

        _updateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _updateCts = cts;
        IsCheckingForUpdate = true;
        IsUpdateResultOpen = false;
        try
        {
            var updated = await _streamResolver.UpdateBackendAsync(cts.Token);
            var version = await _streamResolver.GetBackendVersionAsync(cts.Token);
            YtDlpVersion = version ?? "Not installed";
            ShowUpdateResult(
                InfoBarSeverity.Success,
                updated ? "yt-dlp updated" : "yt-dlp is up to date",
                version is null ? string.Empty : $"Version {version}");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowUpdateResult(InfoBarSeverity.Error, "yt-dlp update failed", ex.Message);
            Notifications.ShowError("yt-dlp update failed", ex);
        }
        finally
        {
            if (ReferenceEquals(_updateCts, cts))
            {
                IsCheckingForUpdate = false;
            }
        }
    }

    // Not tied to this page's lifetime: leaving Settings doesn't stop a download.
    [RelayCommand(CanExecute = nameof(CanCheckForAppUpdate))]
    private async Task CheckForAppUpdateAsync()
    {
        try
        {
            if (await _appUpdates.CheckAsync() == UpdateState.Failed)
            {
                Notifications.Show(new AppNotification(NotificationSeverity.Error, "Couldn't update Hush", _appUpdates.ErrorMessage ?? string.Empty));
            }
        }
        catch (OperationCanceledException)
        {
            // The app is shutting down.
        }
    }

    [RelayCommand]
    private void RestartToUpdate() => _appUpdates.RestartToUpdate();

    [RelayCommand]
    private async Task OpenLastFmApiAccountPageAsync() => await LaunchAsync(LastFmApiAccountPage);

    [RelayCommand(CanExecute = nameof(CanConnectLastFm))]
    private async Task ConnectLastFmAsync() => await RunLastFmAsync("Couldn't connect to Last.fm", async ct =>
    {
        var page = await _lastFm.BeginConnectAsync(LastFmApiKey, LastFmSharedSecret, ct);
        _lastFmApprovalPage = page;
        LastFmApiKey = string.Empty;
        LastFmSharedSecret = string.Empty;
        await LaunchAsync(page);
    });

    [RelayCommand(CanExecute = nameof(CanUseLastFm))]
    private async Task CompleteLastFmConnectAsync() => await RunLastFmAsync("Couldn't connect to Last.fm", async ct =>
    {
        if (!await _lastFm.CompleteConnectAsync(ct))
        {
            LastFmHint = "Last.fm hasn't received your approval yet. Allow access in the browser, then try again.";
        }
    });

    [RelayCommand]
    private async Task ReopenLastFmApprovalPageAsync()
    {
        if (_lastFmApprovalPage is { } page)
        {
            await LaunchAsync(page);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseLastFm))]
    private async Task CancelLastFmConnectAsync() =>
        await RunLastFmAsync("Couldn't cancel the Last.fm connection", ct => _lastFm.DisconnectAsync(ct));

    [RelayCommand(CanExecute = nameof(CanUseLastFm))]
    private async Task DisconnectLastFmAsync() =>
        await RunLastFmAsync("Couldn't disconnect Last.fm", ct => _lastFm.DisconnectAsync(ct));

    private bool CanCheckForAppUpdate() => ShowCheckForAppUpdate && !IsAppUpdateBusy;

    private bool CanUseLastFm() => !IsLastFmWorking;

    private bool CanConnectLastFm() =>
        !IsLastFmWorking && !string.IsNullOrWhiteSpace(LastFmApiKey) && !string.IsNullOrWhiteSpace(LastFmSharedSecret);

    partial void OnThemeChanged(string value)
    {
        if (ThemeOptions.Any(o => o.Value == value))
        {
            Save(s => s.Theme = value);
        }
    }

    partial void OnAccentStyleChanged(string value) => Save(s => s.AccentStyle = value);

    partial void OnDesignSystemChanged(string value)
    {
        if (!DesignSystemOptions.Any(o => o.Value == value))
        {
            return;
        }

        Save(s => s.DesignSystem = value);
        if (_loading)
        {
            return;
        }

        // The automatic accent that fits each design: the Windows colour for Windows, the album art for Hush.
        // An explicitly chosen preset stays as it is.
        if (DesignSystems.IsWindows(value) && AccentStyle == AccentPresets.Artwork)
        {
            AccentStyle = AccentPresets.System;
        }
        else if (!DesignSystems.IsWindows(value) && AccentStyle == AccentPresets.System)
        {
            AccentStyle = AccentPresets.Artwork;
        }
    }

    partial void OnPlayerLayoutChanged(string value)
    {
        if (PlayerLayoutOptions.Any(o => o.Value == value))
        {
            Save(s => s.PlayerLayout = value);
        }
    }

    [RelayCommand]
    private void RestartForDesign()
    {
        var reason = DesignSystems.Restart();
        Notifications.ShowInfo("Couldn't restart Hush", $"Close and reopen Hush to apply the new design ({reason}).");
    }

    partial void OnNowPlayingArtStyleChanged(string value)
    {
        if (NowPlayingArtStyleOptions.Any(o => o.Value == value))
        {
            Save(s => s.NowPlayingArtStyle = value);
        }
    }

    partial void OnReportPlaybackHistoryChanged(bool value) => Save(s => s.ReportPlaybackHistory = value);

    partial void OnUseAccountForStreamsChanged(bool value) => Save(s => s.UseAccountForStreams = value);

    partial void OnNormalizeVolumeChanged(bool value) => Save(s => s.NormalizeVolume = value);

    partial void OnResumeLastSessionChanged(bool value) => Save(s => s.ResumeLastSession = value);

    partial void OnCloseToTrayChanged(bool value) => Save(s => s.CloseToTray = value);

    partial void OnStartWithWindowsChanged(bool value) => Save(s => s.StartWithWindows = value);

    partial void OnShowTaskbarWidgetChanged(bool value) => Save(s => s.ShowTaskbarWidget = value);

    partial void OnLastFmScrobblingChanged(bool value) => Save(s => s.LastFmScrobbling = value);

    partial void OnCheckYtDlpUpdatesOnStartupChanged(bool value) => Save(s => s.CheckYtDlpUpdatesOnStartup = value);

    partial void OnYtDlpPathChanged(string? value) => Save(s => s.YtDlpPath = string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    partial void OnYtDlpJsRuntimeChanged(string value) =>
        Save(s => s.YtDlpJsRuntime = string.IsNullOrWhiteSpace(value) ? "auto" : value.Trim());

    partial void OnContentLocationChanged(string value) =>
        Save(s => s.ContentLocation = (value ?? string.Empty).Trim().ToUpperInvariant());

    partial void OnLogLevelChanged(string value)
    {
        if (Enum.TryParse<LogEventLevel>(value, out var level))
        {
            _levelSwitch.MinimumLevel = level;
        }

        Save(s => s.LogLevel = value);
    }

    private static double Hue(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var chroma = max - Math.Min(r, Math.Min(g, b));
        if (chroma <= 0)
        {
            return 0;
        }

        var hue = max == r ? ((g - b) / chroma) % 6 : max == g ? ((b - r) / chroma) + 2 : ((r - g) / chroma) + 4;
        return (hue * 60 + 360) % 360;
    }

    private IReadOnlyList<AccentSwatch> BuildAccentSwatches()
    {
        var light = _theme.ActualTheme == Microsoft.UI.Xaml.ElementTheme.Light;
        var presets = AccentPresets.All.Select(p => (Preset: p, Color: light ? p.Light : p.Dark)).ToList();

        // "Album art" shows the presets as one hue-ordered gradient: it can be any of them.
        List<AccentSwatch> swatches =
        [
            new(AccentPresets.Artwork, "Album art", "Follows the cover of what's playing", [.. presets.Select(p => p.Color).OrderBy(Hue)], IsArtwork: true),
        ];

        // Left out on the rare system that doesn't report an accent colour.
        if (SystemAccent.Read() is { } system)
        {
            swatches.Add(new(AccentPresets.System, "Windows accent color", string.Empty, [AccentPalette.ForSystem(system, light)]));
        }

        swatches.AddRange(presets.Select(p => new AccentSwatch(p.Preset.Name, p.Preset.Name, p.Preset.Description, [p.Color])));
        return swatches;
    }

    // ThemeChanged is raised on the UI thread.
    private void OnAppThemeChanged(object? sender, EventArgs e) => AccentSwatches = BuildAccentSwatches();

    // The Windows accent colour changed while the page is open. Arrives on a background thread.
    private void OnSystemAccentChanged(object? sender, EventArgs e) => _dispatcher.Run(() => AccentSwatches = BuildAccentSwatches());

    // The taskbar player can turn itself off from its menu while this page is open. Arrives on a background thread.
    private void OnSettingsChanged(object? sender, EventArgs e) => _dispatcher.Run(() =>
    {
        if (ShowTaskbarWidget != _settings.Current.ShowTaskbarWidget)
        {
            _loading = true;
            ShowTaskbarWidget = _settings.Current.ShowTaskbarWidget;
            _loading = false;
        }
    });

    // Last.fm events arrive on background threads.
    private void OnLastFmStateChanged(object? sender, EventArgs e) => _dispatcher.Run(RefreshLastFm);

    // Update events arrive on background threads.
    private void OnAppUpdateStateChanged(object? sender, EventArgs e) => _dispatcher.Run(RefreshAppUpdate);

    private void RefreshAppUpdate()
    {
        var state = _appUpdates.State;
        var version = _appUpdates.AvailableVersion;
        AppUpdateStatus = state switch
        {
            UpdateState.NotInstalled => "Updates are available in installed builds.",
            UpdateState.Disabled => "This build doesn't check for updates.",
            UpdateState.Checking => "Checking for updates…",
            UpdateState.UpToDate => "Hush is up to date.",
            UpdateState.Downloading when _appUpdates.DownloadProgress > 0 => $"Downloading version {version}… {_appUpdates.DownloadProgress}%",
            UpdateState.Downloading => $"Downloading version {version}…",
            UpdateState.ReadyToRestart => $"Version {version} is ready. Restart to update.",
            UpdateState.Failed when version is not null => $"Couldn't download version {version}. {_appUpdates.ErrorMessage}".TrimEnd(),
            UpdateState.Failed => $"Couldn't check for updates. {_appUpdates.ErrorMessage}".TrimEnd(),
            _ => "Hush checks for updates shortly after it starts.",
        };
        IsAppUpdateBusy = state is UpdateState.Checking or UpdateState.Downloading;
        IsAppUpdateReady = state == UpdateState.ReadyToRestart;
        ShowCheckForAppUpdate = _appUpdates.CanCheck && state != UpdateState.ReadyToRestart;
        CheckForAppUpdateCommand.NotifyCanExecuteChanged();
    }

    private void RefreshLastFm()
    {
        var state = _lastFm.State;
        if (state != LastFmState)
        {
            LastFmHint = null;
        }

        LastFmUserName = _lastFm.UserName;
        LastFmPendingScrobbles = _lastFm.PendingScrobbles;
        LastFmState = state;
    }

    private async Task RunLastFmAsync(string errorTitle, Func<CancellationToken, Task> work)
    {
        _lastFmCts?.Cancel();
        var cts = new CancellationTokenSource();
        _lastFmCts = cts;
        IsLastFmWorking = true;
        LastFmHint = null;
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Notifications.ShowError(errorTitle, ex);
        }
        finally
        {
            if (ReferenceEquals(_lastFmCts, cts))
            {
                IsLastFmWorking = false;
            }

            RefreshLastFm();
        }
    }

    private async Task LaunchAsync(Uri uri)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(uri))
            {
                LastFmHint = $"Couldn't open the browser. Go to {uri} yourself.";
            }
        }
        catch (Exception ex)
        {
            Notifications.ShowError("Couldn't open the browser", ex);
        }
    }

    private void ShowUpdateResult(InfoBarSeverity severity, string title, string message)
    {
        UpdateResultSeverity = severity;
        UpdateResultTitle = title;
        UpdateResultMessage = message;
        IsUpdateResultOpen = true;
    }

    private void Save(Action<AppSettings> update)
    {
        if (!_loading)
        {
            _ = SaveAsync(update);
        }
    }

    // Not routed through RunAsync: that cancels the previous run, and two quick toggles would drop a save.
    private async Task SaveAsync(Action<AppSettings> update)
    {
        try
        {
            await _settings.UpdateAsync(update);
        }
        catch (Exception ex)
        {
            Notifications.ShowError("Could not save settings", ex);
        }
    }
}
