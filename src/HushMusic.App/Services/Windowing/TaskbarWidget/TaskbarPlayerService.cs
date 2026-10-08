using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Windows.UI;
using HushMusic.App.Controls.TaskbarFlyout;
using HushMusic.App.Services.Shell;
using HushMusic.App.ViewModels.Shell;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Services;

namespace HushMusic.App.Services.Windowing.TaskbarWidget;

/// <summary>
/// The taskbar player (Settings → Window → "Show player on the taskbar"): runs <see cref="TaskbarWidgetWindow"/> while
/// the setting is on, feeds it snapshots of <see cref="PlayerViewModel"/> (the player bar's state, so volume changes are
/// saved and shown everywhere), carries out its commands and owns its flyout. UI thread, except the
/// <see cref="ITaskbarWidgetSink"/> callbacks, which only queue work for it.
/// </summary>
public sealed class TaskbarPlayerService : ITaskbarWidgetSink, IDisposable
{
    private const int ArtThumbnailWidth = 120;
    private static readonly long ProgressIntervalTicks = TimeSpan.FromSeconds(1).Ticks;

    private readonly PlayerViewModel _player;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAccentColorService _accent;
    private readonly IImageCache _images;
    private readonly IThemeService _theme;
    private readonly ILogger<TaskbarPlayerService> _logger;
    private readonly bool _testInstance = Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_BACKGROUND") == "1";

    private TaskbarWidgetWindow? _widget;
    private TaskbarFlyoutWindow? _flyout;
    private Action? _showMainWindow;
    private WidgetArt? _art;
    private CancellationTokenSource? _artCts;
    private int _coverSize = 32;
    private long _lastProgressTicks;
    private double? _sentProgress;
    private bool _attached;
    private bool _disposed;

    public TaskbarPlayerService(
        PlayerViewModel player,
        ISettingsService settings,
        IUiDispatcher dispatcher,
        IAccentColorService accent,
        IImageCache images,
        IThemeService theme,
        ILogger<TaskbarPlayerService> logger)
    {
        _player = player;
        _settings = settings;
        _dispatcher = dispatcher;
        _accent = accent;
        _images = images;
        _theme = theme;
        _logger = logger;
    }

    /// <summary>
    /// Test instances (HUSHMUSIC_TEST_BACKGROUND=1) only touch the real taskbar with HUSHMUSIC_TEST_TASKBAR=1, so other
    /// automated runs never put a player on the user's taskbar.
    /// </summary>
    private bool Allowed => !_testInstance || Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_TASKBAR") == "1";

    private string? ArtUrl => _player.Track?.ThumbnailFor(ArtThumbnailWidth)?.Url;

    /// <summary>Starts following the setting and the player. UI thread, once, after the main window exists.</summary>
    public void Attach(Action showMainWindow)
    {
        if (_attached || _disposed)
        {
            return;
        }

        _attached = true;
        _showMainWindow = showMainWindow;
        _settings.Changed += OnSettingsChanged;
        _player.PropertyChanged += OnPlayerPropertyChanged;
        _accent.Changed += OnAccentChanged;
        Apply();
    }

    /// <summary>
    /// Test hook (HUSHMUSIC_TEST_FLYOUT=1): opens the flyout without activating it, above where the player sits.
    /// HUSHMUSIC_TEST_FLYOUT_PLAY=&lt;n&gt; also clicks its n-th "Up next" tile 6 s later; HUSHMUSIC_TEST_FLYOUT_HOVER=&lt;n&gt;
    /// shows that tile as hovered (screenshots can't hover).
    /// </summary>
    public void OpenFlyoutForTest()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_FLYOUT_PLAY"), out var tile) && tile > 0)
        {
            Flyout.PlayUpNextForTest(tile, TimeSpan.FromSeconds(6));
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_FLYOUT_HOVER"), out var hover) && hover > 0)
        {
            Flyout.HoverUpNextForTest(hover, TimeSpan.FromSeconds(2));
        }

        var taskbar = WidgetNative.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !Win32.GetWindowRect(taskbar, out var r))
        {
            return;
        }

        var dpi = Win32.GetDpiForWindow(taskbar);
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var bar = new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        var widget = new PixelRect(bar.X + (int)Math.Round(4 * scale), bar.Y, (int)Math.Round(TaskbarWidgetLayout.PreferredWidth * scale), bar.Height);
        Flyout.ShowAt(new TaskbarWidgetAnchor(widget, bar, dpi == 0 ? 96 : dpi), activate: false);
    }

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
            _player.PropertyChanged -= OnPlayerPropertyChanged;
            _accent.Changed -= OnAccentChanged;
        }

        _artCts?.Cancel();

        // Waits briefly so the player is off the taskbar before the process ends.
        _widget?.Dispose(TimeSpan.FromMilliseconds(500));
        _widget = null;

        // A hidden window still counts as open and would keep the app running.
        _flyout?.CloseForGood();
        _flyout = null;
    }

    // ===== ITaskbarWidgetSink (widget thread: queue and return) =====

    void ITaskbarWidgetSink.OnCommand(TaskbarWidgetCommand command) => _dispatcher.Run(() => Execute(command));

    void ITaskbarWidgetSink.OnVolumeNotches(int notches) => _dispatcher.Run(() =>
    {
        _player.Volume = TaskbarWidgetLayout.StepVolume(_player.Volume, notches);
    });

    void ITaskbarWidgetSink.OnToggleFlyout(TaskbarWidgetAnchor anchor) => _dispatcher.Run(() =>
    {
        if (!_disposed && _widget is not null)
        {
            Flyout.Toggle(anchor, activate: !_testInstance);
        }
    });

    void ITaskbarWidgetSink.OnCoverSizeChanged(int pixels) => _dispatcher.Run(() =>
    {
        _coverSize = Math.Max(1, pixels);
        LoadArt();
    });

    private TaskbarFlyoutWindow Flyout => _flyout ??= new TaskbarFlyoutWindow(_theme, _testInstance);

    private void Execute(TaskbarWidgetCommand command)
    {
        if (_disposed)
        {
            return;
        }

        switch (command)
        {
            case TaskbarWidgetCommand.Previous:
                _player.PreviousCommand.Execute(null);
                break;
            case TaskbarWidgetCommand.PlayPause:
                _player.PlayPauseCommand.Execute(null);
                break;
            case TaskbarWidgetCommand.Next:
                _player.NextCommand.Execute(null);
                break;
            case TaskbarWidgetCommand.OpenApp:
                _flyout?.Hide();
                _showMainWindow?.Invoke();
                break;
            case TaskbarWidgetCommand.HidePlayer:
                _ = HidePlayerAsync();
                break;
        }
    }

    private async Task HidePlayerAsync()
    {
        try
        {
            await _settings.UpdateAsync(s => s.ShowTaskbarWidget = false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not turn the taskbar player off");
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _dispatcher.Run(Apply);

    // Starts or stops the widget to match the setting (live, no restart).
    private void Apply()
    {
        if (_disposed)
        {
            return;
        }

        var wanted = _settings.Current.ShowTaskbarWidget && Allowed;
        if (wanted && _widget is null)
        {
            _logger.LogInformation("Taskbar player on");
            _sentProgress = null;
            _widget = TaskbarWidgetWindow.Start(BuildState(), this, _logger);
            LoadArt();
        }
        else if (!wanted && _widget is not null)
        {
            _logger.LogInformation("Taskbar player off");
            _widget.Dispose();
            _widget = null;
            _flyout?.Hide();
        }
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_widget is null)
        {
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Track):
                LoadArt();
                Push();
                break;
            case nameof(PlayerViewModel.PositionSeconds):
                // The line moves at most once a second; a jump (seek) shows at once.
                var progress = Progress();
                var now = DateTime.UtcNow.Ticks;
                if (now - _lastProgressTicks >= ProgressIntervalTicks || (progress is null) != (_sentProgress is null)
                    || Math.Abs((progress ?? 0) - (_sentProgress ?? 0)) > 0.02)
                {
                    Push();
                }

                break;
            case nameof(PlayerViewModel.Status) or nameof(PlayerViewModel.DurationSeconds) or nameof(PlayerViewModel.Volume) or nameof(PlayerViewModel.IsMuted):
                Push();
                break;
        }
    }

    private void OnAccentChanged(object? sender, AccentColorChangedEventArgs e) => Push();

    private void Push()
    {
        if (_widget is null)
        {
            return;
        }

        var state = BuildState();
        _lastProgressTicks = DateTime.UtcNow.Ticks;
        _sentProgress = state.Progress;
        _widget.Update(state);
    }

    private TaskbarWidgetState BuildState()
    {
        var track = _player.Track;
        if (track is null)
        {
            return TaskbarWidgetState.Empty with { Accent = ToArgb(_accent.Current), Volume = _player.Volume };
        }

        var art = _art is { } loaded && loaded.Url == ArtUrl && loaded.Size == _coverSize ? loaded : null;
        return new TaskbarWidgetState(
            HasTrack: true,
            Title: track.Title,
            Artist: track.ArtistsText ?? string.Empty,
            IsPlaying: _player.IsPlaying || _player.IsLoading,
            Progress: Progress(),
            IsLive: TaskbarFlyoutBindings.IsLive(track),
            Volume: _player.Volume,
            IsMuted: _player.IsMuted,
            Accent: ToArgb(_accent.Current),
            Art: art);
    }

    private double? Progress() =>
        _player.HasTrack && _player.DurationSeconds > 0 ? Math.Clamp(_player.PositionSeconds / _player.DurationSeconds, 0, 1) : null;

    private static uint ToArgb(Color color) => ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;

    // Decodes the cover at the size the widget draws it (again after a DPI change), off the UI thread.
    private void LoadArt()
    {
        if (_widget is null)
        {
            return;
        }

        var url = ArtUrl;
        var size = _coverSize;
        if (url is null || (_art is { } current && current.Url == url && current.Size == size))
        {
            return;
        }

        _artCts?.Cancel();
        var cts = new CancellationTokenSource();
        _artCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                var art = await WidgetArtLoader.LoadAsync(_images, url, size, Math.Round(size / 8.0), cts.Token).ConfigureAwait(false);
                _dispatcher.Run(() =>
                {
                    if (!cts.IsCancellationRequested)
                    {
                        _art = art;
                        Push();
                    }
                });
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not load the taskbar player's cover from {Url}", url);
            }
        });
    }
}
