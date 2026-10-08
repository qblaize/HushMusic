using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.Services.Windowing;

/// <summary>
/// Taskbar integration for the main window: Previous / Play-Pause / Next under the thumbnail preview, and the
/// playback position as taskbar progress (paused colour while paused). Player events arrive on background threads
/// and are applied on the UI thread; position updates are throttled to about once a second.
/// </summary>
internal sealed class TaskbarButtons : IDisposable
{
    private const uint ThumbButtonClicked = 0x1800; // THBN_CLICKED
    private const uint PreviousId = 1;
    private const uint PlayPauseId = 2;
    private const uint NextId = 3;
    private const uint MaskIcon = 0x2;
    private const uint MaskTooltip = 0x4;
    private const uint MaskFlags = 0x8;
    private const uint FlagEnabled = 0x0;
    private const uint FlagDisabled = 0x1;
    private static readonly long ProgressIntervalTicks = TimeSpan.FromSeconds(1).Ticks;

    private readonly IntPtr _hwnd;
    private readonly IPlayer _player;
    private readonly IUiDispatcher _dispatcher;
    private readonly PlaybackRemote _remote;
    private readonly ILogger _logger;
    private readonly uint _buttonCreatedMessage;
    private TaskbarList? _taskbar;
    private IntPtr _previousIcon;
    private IntPtr _playIcon;
    private IntPtr _pauseIcon;
    private IntPtr _nextIcon;
    private (bool HasTrack, bool IsPlaying)? _shownButtons;
    private TaskbarProgressState? _shownState;
    private long _lastProgressTicks;
    private bool _disposed;

    public TaskbarButtons(IntPtr hwnd, IPlayer player, IUiDispatcher dispatcher, PlaybackRemote remote, ILogger logger)
    {
        _hwnd = hwnd;
        _player = player;
        _dispatcher = dispatcher;
        _remote = remote;
        _logger = logger;
        _buttonCreatedMessage = Win32.RegisterWindowMessage("TaskbarButtonCreated");
        _player.StatusChanged += OnPlayerChanged;
        _player.TrackChanged += OnPlayerChanged;
        _player.PositionChanged += OnPositionChanged;
    }

    /// <summary>Main-window message filter (see <see cref="WindowMessageHook"/>). UI thread.</summary>
    public bool HandleMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _buttonCreatedMessage && message != 0)
        {
            // Sent whenever the window gets a taskbar button: first show, show after hiding, Explorer restart.
            OnTaskbarButtonCreated();
            return false;
        }

        if (message != Win32.WmCommand || (uint)Win32.HighWord(wParam) != ThumbButtonClicked)
        {
            return false;
        }

        switch ((uint)Win32.LowWord(wParam))
        {
            case PreviousId:
                _remote.Previous();
                return true;
            case PlayPauseId:
                _remote.PlayPause();
                return true;
            case NextId:
                _remote.Next();
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _player.StatusChanged -= OnPlayerChanged;
        _player.TrackChanged -= OnPlayerChanged;
        _player.PositionChanged -= OnPositionChanged;
        _taskbar?.Dispose();
        _taskbar = null;
        foreach (var icon in new[] { _previousIcon, _playIcon, _pauseIcon, _nextIcon })
        {
            if (icon != IntPtr.Zero)
            {
                Win32.DestroyIcon(icon);
            }
        }
    }

    private void OnPlayerChanged(object? sender, EventArgs e) => _dispatcher.Run(Refresh);

    private void OnPositionChanged(object? sender, PositionChangedEventArgs e)
    {
        var now = DateTime.UtcNow.Ticks;
        if (now - Interlocked.Read(ref _lastProgressTicks) < ProgressIntervalTicks)
        {
            return;
        }

        Interlocked.Exchange(ref _lastProgressTicks, now);
        _dispatcher.Run(UpdateProgress);
    }

    private void OnTaskbarButtonCreated()
    {
        if (_disposed)
        {
            return;
        }

        if (_taskbar is null)
        {
            _taskbar = TaskbarList.TryCreate(out var created);
            if (_taskbar is null)
            {
                _logger.LogWarning("The taskbar is unavailable (0x{Result:X8}); no thumbnail buttons or progress", created);
                return;
            }

            LoadIcons();
        }

        var buttons = BuildButtons();
        var result = _taskbar.AddThumbButtons(_hwnd, buttons);
        if (result < 0)
        {
            // The toolbar can only be added once per taskbar button; it may still be there.
            result = _taskbar.UpdateThumbButtons(_hwnd, buttons);
        }

        _logger.LogInformation("Taskbar thumbnail buttons ready (0x{Result:X8})", result);
        _shownState = null;
        UpdateProgress();
    }

    private void Refresh()
    {
        if (_disposed || _taskbar is null)
        {
            return;
        }

        var state = (_remote.HasTrack, _remote.IsPlaying);
        if (_shownButtons != state)
        {
            var result = _taskbar.UpdateThumbButtons(_hwnd, BuildButtons());
            _logger.LogDebug("Taskbar buttons updated: track {HasTrack}, playing {IsPlaying} (0x{Result:X8})", state.HasTrack, state.IsPlaying, result);
        }

        UpdateProgress();
    }

    private void UpdateProgress()
    {
        if (_disposed || _taskbar is null)
        {
            return;
        }

        Interlocked.Exchange(ref _lastProgressTicks, DateTime.UtcNow.Ticks);
        var duration = _player.Duration > TimeSpan.Zero ? _player.Duration : _player.CurrentTrack?.Duration ?? TimeSpan.Zero;
        var state = _player.CurrentTrack is null || duration <= TimeSpan.Zero
            ? TaskbarProgressState.None
            : _player.Status switch
            {
                PlaybackStatus.Playing or PlaybackStatus.Loading or PlaybackStatus.Buffering => TaskbarProgressState.Normal,
                PlaybackStatus.Paused => TaskbarProgressState.Paused,
                _ => TaskbarProgressState.None,
            };

        if (state != _shownState)
        {
            var result = _taskbar.SetProgressState(_hwnd, state);
            _logger.LogDebug("Taskbar progress {State} (0x{Result:X8})", state, result);
            _shownState = state;
        }

        // Setting a value also switches "no progress" to "normal", so only while a bar is shown.
        if (state != TaskbarProgressState.None)
        {
            var total = (ulong)duration.TotalMilliseconds;
            var done = (ulong)Math.Clamp(_player.Position.TotalMilliseconds, 0, total);
            _taskbar.SetProgressValue(_hwnd, done, total);
        }
    }

    private Win32.ThumbButton[] BuildButtons()
    {
        var hasTrack = _remote.HasTrack;
        var playing = _remote.IsPlaying;
        _shownButtons = (hasTrack, playing);
        var flags = hasTrack ? FlagEnabled : FlagDisabled;
        return
        [
            MakeButton(PreviousId, _previousIcon, "Previous", flags),
            MakeButton(PlayPauseId, playing ? _pauseIcon : _playIcon, playing ? "Pause" : "Play", flags),
            MakeButton(NextId, _nextIcon, "Next", flags),
        ];
    }

    private static Win32.ThumbButton MakeButton(uint id, IntPtr icon, string tip, uint flags) => new()
    {
        Mask = MaskIcon | MaskTooltip | MaskFlags,
        Id = id,
        Icon = icon,
        Tip = tip,
        Flags = flags,
    };

    private void LoadIcons()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Taskbar");
        var dpi = Win32.GetDpiForWindow(_hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        _previousIcon = Win32.LoadSmallIcon(Path.Combine(folder, "Previous.ico"), dpi);
        _playIcon = Win32.LoadSmallIcon(Path.Combine(folder, "Play.ico"), dpi);
        _pauseIcon = Win32.LoadSmallIcon(Path.Combine(folder, "Pause.ico"), dpi);
        _nextIcon = Win32.LoadSmallIcon(Path.Combine(folder, "Next.ico"), dpi);
        if (_previousIcon == IntPtr.Zero || _playIcon == IntPtr.Zero || _pauseIcon == IntPtr.Zero || _nextIcon == IntPtr.Zero)
        {
            _logger.LogWarning("Some taskbar button icons are missing from {Folder}", folder);
        }
    }
}
