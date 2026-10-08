using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Automation.Peers;
using Windows.System;
using HushMusic.App.Services.Shell;
using HushMusic.App.ViewModels.NowPlaying;
using HushMusic.App.ViewModels.Shell;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.ViewModels;

public sealed partial class ShellViewModel : ObservableObject
{
    private static readonly TimeSpan AutoDismissDelay = TimeSpan.FromSeconds(6);

    private readonly INavigationService _navigation;
    private readonly IUiDispatcher _dispatcher;
    private readonly ISignInCoordinator _signIn;
    private readonly IAccentColorService _accent;
    private readonly ILogger<ShellViewModel> _logger;
    private bool _initialized;

    public ShellViewModel(
        INavigationService navigation,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        ISignInCoordinator signIn,
        IAccentColorService accent,
        AccountViewModel account,
        PlayerViewModel player,
        NowPlayingViewModel nowPlaying,
        SearchBoxViewModel search,
        ILogger<ShellViewModel> logger)
    {
        _navigation = navigation;
        _dispatcher = dispatcher;
        _signIn = signIn;
        _accent = accent;
        _logger = logger;
        Account = account;
        Player = player;
        NowPlaying = nowPlaying;
        Search = search;

        _navigation.Navigated += (_, key) =>
        {
            CanGoBack = _navigation.CanGoBack;
            CurrentPage = key;
        };
        notifications.Raised += (_, notification) => _dispatcher.Run(() => AddNotification(notification));
    }

    public AccountViewModel Account { get; }

    public PlayerViewModel Player { get; }

    /// <summary>The full-window Now Playing view (player bar artwork/title, lyrics and queue buttons).</summary>
    public NowPlayingViewModel NowPlaying { get; }

    /// <summary>The search modal (rail Search button, Ctrl+F).</summary>
    public SearchBoxViewModel Search { get; }

    public ObservableCollection<NotificationViewModel> Notifications { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GoBackCommand))]
    public partial bool CanGoBack { get; set; }

    [ObservableProperty]
    public partial PageKey? CurrentPage { get; set; }

    /// <summary>Startup work for the shell: player state, accent colour, saved sign-in, leftover sign-in browser profile. Never throws.</summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            Player.Initialize();
            try
            {
                _accent.Start();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The album-art accent could not start");
            }

            if (!_signIn.IsSignInWindowOpen)
            {
                _ = _signIn.DeleteBrowserProfileAsync();
            }

            await Account.InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Shell initialization failed");
        }
    }

    public void Navigate(PageKey page, object? parameter = null) => _navigation.NavigateTo(page, parameter);

    /// <summary>
    /// Global keyboard shortcuts. Returns true when the key was handled.
    /// Space = play/pause, Ctrl+Left/Right = previous/next, Ctrl+F = search, Alt+Left = back (closes Now Playing first),
    /// Esc = close Now Playing.
    /// </summary>
    public bool HandleShortcut(VirtualKey key, VirtualKeyModifiers modifiers, ShortcutFocus focus, bool isRepeat)
    {
        // The search modal sits above Now Playing and handles its own Esc.
        if (key == VirtualKey.Escape && modifiers == VirtualKeyModifiers.None && NowPlaying.IsOpen && !Search.IsOpen)
        {
            NowPlaying.Close();
            return true;
        }

        if (key == VirtualKey.Space && modifiers == VirtualKeyModifiers.None)
        {
            if (focus != ShortcutFocus.None)
            {
                return false;
            }

            // Holding Space must not toggle playback over and over.
            if (!isRepeat)
            {
                Player.PlayPauseCommand.Execute(null);
            }

            return true;
        }

        if (key == VirtualKey.Left && modifiers == VirtualKeyModifiers.Menu && !Search.IsOpen)
        {
            if (!isRepeat)
            {
                Back();
            }

            return true;
        }

        if (modifiers != VirtualKeyModifiers.Control)
        {
            return false;
        }

        switch (key)
        {
            case VirtualKey.F:
                Search.Open();
                return true;
            case VirtualKey.Left when focus != ShortcutFocus.TextInput:
                if (!isRepeat)
                {
                    Player.PreviousCommand.Execute(null);
                }

                return true;
            case VirtualKey.Right when focus != ShortcutFocus.TextInput:
                if (!isRepeat)
                {
                    Player.NextCommand.Execute(null);
                }

                return true;
            default:
                return false;
        }
    }

    /// <summary>Alt+Left and the mouse back button: closes Now Playing when it is open, otherwise goes back a page.</summary>
    public void Back()
    {
        if (NowPlaying.IsOpen)
        {
            NowPlaying.Close();
        }
        else
        {
            _navigation.GoBack();
        }
    }

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void GoBack() => _navigation.GoBack();

    /// <summary>Rail items: the parameter is a <see cref="PageKey"/> name.</summary>
    [RelayCommand]
    private void NavigateTo(string? page)
    {
        if (Enum.TryParse<PageKey>(page, out var key))
        {
            // The rail stays usable under Now Playing; picking the page already shown raises no Navigated event.
            NowPlaying.Close();
            Navigate(key);
        }
    }

    [RelayCommand]
    private void OpenSearch() => Search.Open();

    private void AddNotification(AppNotification notification)
    {
        var item = new NotificationViewModel(notification, n => Notifications.Remove(n));
        Notifications.Add(item);
        while (Notifications.Count > 3)
        {
            Notifications.RemoveAt(0);
        }

        if (notification.Severity != NotificationSeverity.Error && notification.Action is null)
        {
            _ = DismissLaterAsync(item);
        }
    }

    private async Task DismissLaterAsync(NotificationViewModel item)
    {
        await Task.Delay(AutoDismissDelay);
        _dispatcher.Run(() => Notifications.Remove(item));
    }
}

/// <summary>One floating toast. Errors and toasts with an action stay until closed; everything else dismisses itself.</summary>
public sealed class NotificationViewModel(AppNotification notification, Action<NotificationViewModel> onDismiss)
{
    public string Title { get; } = notification.Title;

    public string Message { get; } = notification.Message;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public NotificationSeverity Severity { get; } = notification.Severity;

    public string Glyph { get; } = notification.Severity switch
    {
        NotificationSeverity.Error => "",
        NotificationSeverity.Warning => "",
        NotificationSeverity.Success => "",
        _ => "",
    };

    /// <summary>Errors interrupt the screen reader; the rest wait their turn.</summary>
    public AutomationLiveSetting LiveSetting { get; } =
        notification.Severity == NotificationSeverity.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite;

    public bool HasAction => notification.Action is not null;

    public string ActionLabel { get; } = notification.Action?.Label ?? string.Empty;

    public void Dismiss() => onDismiss(this);

    public void InvokeAction()
    {
        Dismiss();
        notification.Action?.Invoke();
    }
}
