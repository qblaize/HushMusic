using Microsoft.Extensions.Logging;
using Microsoft.Windows.AppNotifications;
using Windows.Graphics.Imaging;
using WindowsNotification = Microsoft.Windows.AppNotifications.AppNotification;
using HushMusic.App.Services.Shell;
using HushMusic.App.Services.Windowing;
using HushMusic.App.Services.Windowing.TaskbarWidget;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.Core.Services;

namespace HushMusic.App.Services.Notifications;

/// <summary>
/// Song notifications (Settings → Window → Song notifications): a Windows notification with the cover, title, artist
/// and a Next button when a new song starts, or the song on a live station changes, while Hush isn't the window in
/// front. Each one replaces the last (same tag and group) and expires soon, so Notification Center never fills up.
/// <para>
/// Shown with the Windows App SDK's AppNotificationManager. This app is unpackaged, so it registers itself (display
/// name, icon, activation) the first time the setting is on, unregisters on exit, and the uninstaller removes the
/// registration (<see cref="RemoveRegistration"/>). Test instances never register: they log what they would show.
/// </para>
/// </summary>
public sealed class TrackNotificationService : IDisposable
{
    private const string Tag = "now-playing";
    private const string Group = "playback";
    private const int CoverPixels = 192;
    private const int TrackThumbnailWidth = 226;
    private const int KeptCovers = 3;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    // A station's first notification waits a moment for its song title, so starting a station shows one, not two.
    private static readonly TimeSpan StationSongWait = TimeSpan.FromSeconds(4);

    private readonly IPlayer _player;
    private readonly IRadioNowPlaying _radio;
    private readonly ISettingsService _settings;
    private readonly IImageCache _images;
    private readonly IAppPaths _paths;
    private readonly IUiDispatcher _dispatcher;
    private readonly INotificationService _notifications;
    private readonly ILogger<TrackNotificationService> _logger;
    private readonly bool _testInstance = Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_BACKGROUND") == "1";
    private readonly Lock _gate = new();

    private Action? _showMainWindow;
    private Action? _next;
    private CancellationTokenSource? _pending;
    private string? _lastKey;
    private bool _registered;
    private bool _registrationFailed;
    private bool _showFailureLogged;
    private bool _attached;
    private bool _disposed;

    public TrackNotificationService(
        IPlayer player,
        IRadioNowPlaying radio,
        ISettingsService settings,
        IImageCache images,
        IAppPaths paths,
        IUiDispatcher dispatcher,
        INotificationService notifications,
        ILogger<TrackNotificationService> logger)
    {
        _player = player;
        _radio = radio;
        _settings = settings;
        _images = images;
        _paths = paths;
        _dispatcher = dispatcher;
        _notifications = notifications;
        _logger = logger;
    }

    private bool Enabled => _settings.Current.ShowTrackNotifications && !_disposed;

    /// <summary>
    /// Removes this install's notification registration (uninstall hook). Runs before anything else in the process;
    /// never throws.
    /// </summary>
    public static void RemoveRegistration()
    {
        try
        {
            AppNotificationManager.Default.UnregisterAll();
        }
        catch (Exception)
        {
            // Never fail the uninstall over this.
        }
    }

    /// <summary>Starts following the player and the setting. UI thread, once, after the main window exists.</summary>
    internal void Attach(Action showMainWindow, Action next)
    {
        if (_attached || _disposed)
        {
            return;
        }

        _attached = true;
        _showMainWindow = showMainWindow;
        _next = next;
        _settings.Changed += OnSettingsChanged;
        _player.TrackStarted += OnTrackStarted;
        _radio.Changed += OnRadioChanged;
        ApplySetting();
    }

    /// <summary>A notification was clicked while another copy of the app was starting (it handed the click over). Any thread.</summary>
    internal void OnRedirectedActivation(string? argument) => Run(TrackNotificationContent.Parse(argument));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_attached)
        {
            _settings.Changed -= OnSettingsChanged;
            _player.TrackStarted -= OnTrackStarted;
            _radio.Changed -= OnRadioChanged;
        }

        lock (_gate)
        {
            _pending?.Cancel();
        }

        if (!_registered)
        {
            return;
        }

        try
        {
            // The song is over once the app is: don't leave a Next button behind.
            AppNotificationManager.Default.RemoveByTagAndGroupAsync(Tag, Group).AsTask().Wait(TimeSpan.FromMilliseconds(500));
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            AppNotificationManager.Default.Unregister();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not unregister song notifications");
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _dispatcher.Run(ApplySetting);

    private void ApplySetting()
    {
        if (Enabled)
        {
            EnsureRegistered();
            return;
        }

        lock (_gate)
        {
            _pending?.Cancel();
            _lastKey = null;
        }

        if (_registered)
        {
            _ = RemoveShownAsync();
        }
    }

    private bool EnsureRegistered()
    {
        if (_registered || _testInstance)
        {
            return _registered;
        }

        if (_registrationFailed)
        {
            return false;
        }

        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                throw new NotSupportedException("App notifications aren't supported here.");
            }

            // The handler must be in place before Register, or a click starts a second copy of the app.
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            if (Win32.IsPackaged())
            {
                // MSIX: name, icon and activation come from the package manifest.
                manager.Register();
            }
            else
            {
                var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.targetsize-48_altform-unplated.png");
                manager.Register("Hush", new Uri(icon));
            }

            _registered = true;
            _logger.LogInformation("Song notifications registered (Windows setting: {Setting})", manager.Setting);
        }
        catch (Exception ex)
        {
            _registrationFailed = true;
            _logger.LogWarning(ex, "Could not register for song notifications");
            _notifications.ShowInfo("Song notifications aren't available", "Windows didn't let Hush show notifications.");
        }

        return _registered;
    }

    private async Task RemoveShownAsync()
    {
        try
        {
            await AppNotificationManager.Default.RemoveByTagAndGroupAsync(Tag, Group);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove the song notification");
        }
    }

    // ===== Triggers (player and radio events arrive on background threads) =====

    private void OnTrackStarted(object? sender, TrackChangedEventArgs e)
    {
        if (!Enabled || e.Track is not { } track)
        {
            return;
        }

        var waitForSong = track.IsLiveRadio && !LiveRadio.IsFor(track, _radio.Current);
        Schedule(waitForSong ? StationSongWait : TimeSpan.Zero);
    }

    private void OnRadioChanged(object? sender, RadioNowPlayingChangedEventArgs e)
    {
        if (Enabled && e.NowPlaying is not null && _player.CurrentTrack?.Station?.Id == e.StationId)
        {
            Schedule(TimeSpan.Zero);
        }
    }

    // The latest trigger wins: a pending notification for a song that's already gone is dropped.
    private void Schedule(TimeSpan delay)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _pending?.Cancel();
            cts = _pending = new CancellationTokenSource();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cts.Token).ConfigureAwait(false);
                }

                await ShowAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not show the song notification");
            }
        });
    }

    private async Task ShowAsync(CancellationToken cancellationToken)
    {
        if (!Enabled || _player.CurrentTrack is not { } track)
        {
            return;
        }

        var imageUrl = track.IsLiveRadio ? track.Station?.LogoUrl : track.ThumbnailFor(TrackThumbnailWidth)?.Url;
        var content = TrackNotification.For(track, _radio.Current, imageUrl);
        lock (_gate)
        {
            if (content.Key == _lastKey)
            {
                return;
            }

            _lastKey = content.Key;
        }

        // Only while Hush is in the background: in front, the player bar already shows the song.
        if (Win32.IsForegroundInThisProcess())
        {
            _logger.LogDebug("Song notification skipped: Hush is in front");
            return;
        }

        var cover = await CreateCoverAsync(content.ImageUrl, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = TrackNotificationContent.Build(content, cover, nextButton: true);
        if (_testInstance)
        {
            // Automated test instances never put notifications on the user's screen.
            _logger.LogInformation("Song notification (test instance, not shown): {Payload}", payload);
            return;
        }

        if (!EnsureRegistered())
        {
            return;
        }

        var notification = new WindowsNotification(payload)
        {
            Tag = Tag,
            Group = Group,
            Expiration = DateTimeOffset.Now + Lifetime,
            ExpiresOnReboot = true,
        };
        AppNotificationManager.Default.Show(notification);
        if (notification.Id == 0 && !_showFailureLogged)
        {
            // Turned off in Windows (Settings → System → Notifications) or by Focus.
            _showFailureLogged = true;
            _logger.LogInformation("Windows didn't show the song notification (setting: {Setting})", AppNotificationManager.Default.Setting);
        }
    }

    // A square PNG of the cover in the cache folder: notifications only take local files from an unpackaged app.
    private async Task<string?> CreateCoverAsync(string? url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        try
        {
            var source = await _images.GetLocalPathAsync(url, cancellationToken).ConfigureAwait(false);
            if (source is null)
            {
                return null;
            }

            var pixels = await WidgetArtLoader.DecodeAsync(source, CoverPixels, cancellationToken).ConfigureAwait(false);
            WidgetArtLoader.RoundCorners(pixels, CoverPixels, CoverPixels / 12.0);

            var folder = Directory.CreateDirectory(Path.Combine(_paths.Cache, "notifications")).FullName;
            var path = Path.Combine(folder, $"cover-{DateTime.UtcNow.Ticks}.png");
            await using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream()).AsTask(cancellationToken).ConfigureAwait(false);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, CoverPixels, CoverPixels, 96, 96, pixels);
                await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
            }

            DeleteOldCovers(folder);
            return path;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Some station logos aren't images Windows can decode: the notification goes without one.
            _logger.LogDebug(ex, "No cover for the song notification from {Url}", url);
            return null;
        }
    }

    // Each notification gets a file of its own (Windows may read it after the next one is written); keep the last few.
    private void DeleteOldCovers(string folder)
    {
        try
        {
            foreach (var old in new DirectoryInfo(folder).GetFiles("cover-*.png").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(KeptCovers))
            {
                old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not tidy the notification covers");
        }
    }

    // Raised on a background thread when a notification or its button is clicked while the app runs.
    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        Run(TrackNotificationContent.Parse(args.Argument));

    private void Run(TrackNotificationCommand command) => _dispatcher.Run(() =>
    {
        switch (command)
        {
            case TrackNotificationCommand.Next:
                _next?.Invoke();
                break;
            case TrackNotificationCommand.Show:
                _showMainWindow?.Invoke();
                break;
        }
    });
}
