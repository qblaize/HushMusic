using CommunityToolkit.Mvvm.ComponentModel;
using HushMusic.App.Services.Hotkeys;
using HushMusic.Core.Services;

namespace HushMusic.App.ViewModels;

/// <summary>One row of Settings → Global shortcuts: an action, its keys and whether they work.</summary>
public sealed partial class HotkeySettingViewModel : ObservableObject
{
    private readonly Action<HotkeySettingViewModel> _changed;
    private readonly Func<IDisposable> _pause;
    private IDisposable? _paused;
    private bool _syncing;

    internal HotkeySettingViewModel(HotkeyAction action, Action<HotkeySettingViewModel> changed, Func<IDisposable> pause)
    {
        Action = action;
        Title = TitleOf(action);
        _changed = changed;
        _pause = pause;
    }

    public HotkeyAction Action { get; }

    public string Title { get; }

    /// <summary>The keys as saved ("Ctrl+Alt+Right"), or empty for none. The capture box writes it.</summary>
    [ObservableProperty]
    public partial string Gesture { get; set; } = string.Empty;

    /// <summary>Why the keys don't work (taken by another app, used twice), or null.</summary>
    [ObservableProperty]
    public partial string? Problem { get; set; }

    /// <summary>The capture box is waiting for keys: every global shortcut is released meanwhile, so the keys reach it.</summary>
    [ObservableProperty]
    public partial bool IsCapturing { get; set; }

    public static string TitleOf(HotkeyAction action) => action switch
    {
        HotkeyAction.PlayPause => "Play / pause",
        HotkeyAction.Next => "Next song",
        HotkeyAction.Previous => "Previous song",
        HotkeyAction.VolumeUp => "Volume up",
        HotkeyAction.VolumeDown => "Volume down",
        HotkeyAction.Mute => "Mute / unmute",
        HotkeyAction.Like => "Like / unlike the song",
        HotkeyAction.ShowHide => "Show / hide Hush",
        _ => action.ToString(),
    };

    /// <summary>Shows the saved keys and their state without saving anything back.</summary>
    internal void Sync(HotkeyGesture? gesture, HotkeyStatus? status, bool enabled)
    {
        _syncing = true;
        try
        {
            Gesture = GlobalHotkeyMap.ToSetting(gesture);
            Problem = enabled && status is not null && status.Gesture == gesture ? ProblemOf(status) : null;
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Leaving the page while recording keys.</summary>
    internal void EndCapture()
    {
        IsCapturing = false;
        _paused?.Dispose();
        _paused = null;
    }

    partial void OnGestureChanged(string value)
    {
        if (!_syncing)
        {
            _changed(this);
        }
    }

    partial void OnIsCapturingChanged(bool value)
    {
        if (value)
        {
            _paused ??= _pause();
        }
        else
        {
            _paused?.Dispose();
            _paused = null;
        }
    }

    private static string? ProblemOf(HotkeyStatus status) => status.State switch
    {
        HotkeyState.InUse => "Another app already uses these keys. Choose different ones.",
        HotkeyState.Conflict when status.ConflictsWith is { } owner => $"\"{TitleOf(owner)}\" uses the same keys, so only that one works.",
        HotkeyState.Failed => "Windows didn't accept these keys. Choose different ones.",
        _ => null,
    };
}
