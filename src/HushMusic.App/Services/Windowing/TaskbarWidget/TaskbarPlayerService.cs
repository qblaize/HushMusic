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
/// The taskbar player (Settings → Window → "Show player on the taskbar"): runs a <see cref="TaskbarWidgetWindow"/> on
/// each taskbar the "Taskbar player on" setting picks (the main one, all, or one display's) while the setting is on,
/// feeds them snapshots of <see cref="PlayerViewModel"/> (the player bar's state, so volume changes are saved and shown
/// everywhere), carries out their commands and owns the flyout. UI thread, except the <see cref="ITaskbarWidgetSink"/>
/// callbacks, which only queue work for it.
/// </summary>
public sealed class TaskbarPlayerService : IDisposable
{
    private const int ArtThumbnailWidth = 120;
    private static readonly long ProgressIntervalTicks = TimeSpan.FromSeconds(1).Ticks;

    // Displays settle (and their taskbars appear) a moment after WM_DISPLAYCHANGE.
    private static readonly TimeSpan DisplaySettleDelay = TimeSpan.FromSeconds(1.5);

    private readonly PlayerViewModel _player;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAccentColorService _accent;
    private readonly IImageCache _images;
    private readonly IThemeService _theme;
    private readonly ILogger<TaskbarPlayerService> _logger;
    private readonly bool _testInstance = Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_BACKGROUND") == "1";

    // One per taskbar, keyed by display device name ("" for the main taskbar).
    private readonly Dictionary<string, Widget> _widgets = new(StringComparer.OrdinalIgnoreCase);

    // The cover decoded at each size a widget draws it (taskbars on displays with different scaling differ).
    private readonly Dictionary<int, WidgetArt> _art = [];
    private readonly HashSet<int> _artLoading = [];
    private TaskbarFlyoutWindow? _flyout;
    private TaskbarWidgetAnchor? _flyoutAnchor;
    private Action? _showMainWindow;
    private string? _artUrl;
    private CancellationTokenSource? _artCts;
    private long _lastProgressTicks;
    private double? _sentProgress;
    private int _displayCheckPending;
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

        // Asks every widget to go first, then waits briefly so they are all off the taskbars before the process ends.
        foreach (var widget in _widgets.Values)
        {
            widget.Window.Dispose();
        }

        foreach (var widget in _widgets.Values)
        {
            widget.Window.Dispose(TimeSpan.FromMilliseconds(500));
        }

        _widgets.Clear();

        // A hidden window still counts as open and would keep the app running.
        _flyout?.CloseForGood();
        _flyout = null;
    }

    /// <summary>The connected displays, for the "Taskbar player on" choice.</summary>
    public static IReadOnlyList<DisplayInfo> Displays() => TaskbarWindows.Displays();

    // ===== Widget callbacks, on the UI thread =====

    private void OnToggleFlyout(TaskbarWidgetAnchor anchor)
    {
        if (_disposed || _widgets.Count == 0)
        {
            return;
        }

        // A click on another taskbar's player opens the flyout there at once (the click itself just closed it).
        var sameTaskbar = _flyoutAnchor is { } last && last.Taskbar == anchor.Taskbar;
        _flyoutAnchor = anchor;
        if (sameTaskbar)
        {
            Flyout.Toggle(anchor, activate: !_testInstance);
        }
        else
        {
            Flyout.ShowAt(anchor, activate: !_testInstance);
        }
    }

    private void OnCoverSizeChanged(Widget widget, int pixels)
    {
        if (!_disposed && _widgets.ContainsValue(widget))
        {
            widget.CoverSize = Math.Max(1, pixels);
            LoadArt();
        }
    }

    // Any thread (every widget hears WM_DISPLAYCHANGE): one check, once the displays have settled.
    private void OnDisplaysChanged()
    {
        if (Interlocked.Exchange(ref _displayCheckPending, 1) == 1)
        {
            return;
        }

        _ = Task.Delay(DisplaySettleDelay).ContinueWith(
            _ => _dispatcher.Run(() =>
            {
                Volatile.Write(ref _displayCheckPending, 0);
                Apply();
            }),
            TaskScheduler.Default);
    }

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

    // Starts and stops widgets to match the settings (live, no restart): one per chosen taskbar.
    private void Apply()
    {
        if (_disposed)
        {
            return;
        }

        var current = _settings.Current;
        IReadOnlyList<string?> targets = [];
        if (current.ShowTaskbarWidget && Allowed)
        {
            var choice = TaskbarDisplayChoice.Normalize(current.TaskbarWidgetDisplays);
            targets = choice == TaskbarDisplayChoice.Primary ? [null] : TaskbarDisplayChoice.Targets(choice, Displays());
        }

        var wanted = targets.ToDictionary(t => t ?? string.Empty, t => t, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, widget) in _widgets.ToList())
        {
            if (!wanted.ContainsKey(key))
            {
                _logger.LogInformation("Taskbar player off ({Taskbar})", Describe(widget.Display));
                widget.Window.Dispose();
                _widgets.Remove(key);
            }
        }

        foreach (var (key, display) in wanted)
        {
            if (!_widgets.ContainsKey(key))
            {
                _logger.LogInformation("Taskbar player on ({Taskbar})", Describe(display));
                var widget = new Widget(this, display);
                _widgets[key] = widget;
                widget.Window = TaskbarWidgetWindow.Start(display, BuildState(widget.CoverSize), widget, _logger);
                _sentProgress = null;
            }
        }

        if (_widgets.Count == 0)
        {
            _flyout?.Hide();
            _flyoutAnchor = null;
        }
        else
        {
            LoadArt();
        }
    }

    private static string Describe(string? display) => display is null ? "main taskbar" : TaskbarDisplayChoice.ShortName(display);

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_widgets.Count == 0)
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
        if (_widgets.Count == 0)
        {
            return;
        }

        _lastProgressTicks = DateTime.UtcNow.Ticks;
        _sentProgress = Progress();
        foreach (var widget in _widgets.Values)
        {
            widget.Window.Update(BuildState(widget.CoverSize));
        }
    }

    private TaskbarWidgetState BuildState(int coverSize)
    {
        var track = _player.Track;
        if (track is null)
        {
            return TaskbarWidgetState.Empty with { Accent = ToArgb(_accent.Current), Volume = _player.Volume };
        }

        var art = _art.TryGetValue(coverSize, out var loaded) && loaded.Url == ArtUrl ? loaded : null;
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

    // Decodes the cover at each size a widget draws it (again after a DPI change), off the UI thread.
    private void LoadArt()
    {
        var url = ArtUrl;
        if (_widgets.Count == 0 || url is null)
        {
            return;
        }

        if (url != _artUrl || _artCts is null)
        {
            _artCts?.Cancel();
            _artCts = new CancellationTokenSource();
            _artUrl = url;
            _artLoading.Clear();
        }

        var cts = _artCts;
        foreach (var size in _widgets.Values.Select(w => w.CoverSize).Distinct().ToList())
        {
            if ((_art.TryGetValue(size, out var current) && current.Url == url) || !_artLoading.Add(size))
            {
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var art = await WidgetArtLoader.LoadAsync(_images, url, size, Math.Round(size / 8.0), cts.Token).ConfigureAwait(false);
                    _dispatcher.Run(() =>
                    {
                        if (!cts.IsCancellationRequested)
                        {
                            _artLoading.Remove(size);
                            if (art is not null)
                            {
                                _art[size] = art;
                            }

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

    /// <summary>One taskbar's player and its callbacks, which arrive on its widget thread: they queue the work and return.</summary>
    private sealed class Widget(TaskbarPlayerService owner, string? display) : ITaskbarWidgetSink
    {
        /// <summary>Its display's device name, or null for the main taskbar.</summary>
        public string? Display { get; } = display;

        public TaskbarWidgetWindow Window { get; set; } = null!;

        public int CoverSize { get; set; } = 32;

        public void OnCommand(TaskbarWidgetCommand command) => owner._dispatcher.Run(() => owner.Execute(command));

        public void OnVolumeNotches(int notches) => owner._dispatcher.Run(() =>
            owner._player.Volume = TaskbarWidgetLayout.StepVolume(owner._player.Volume, notches));

        public void OnToggleFlyout(TaskbarWidgetAnchor anchor) => owner._dispatcher.Run(() => owner.OnToggleFlyout(anchor));

        public void OnCoverSizeChanged(int pixels) => owner._dispatcher.Run(() => owner.OnCoverSizeChanged(this, pixels));

        public void OnDisplaysChanged() => owner.OnDisplaysChanged();
    }
}
