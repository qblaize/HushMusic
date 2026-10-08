using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using HushMusic.App.Services.Shell;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.ViewModels.Shell;

/// <summary>Sign-in state for the rail account button and the Settings page. Singleton.</summary>
public sealed partial class AccountViewModel : ObservableObject
{
    private const string FallbackName = "Google account";

    private readonly IAuthService _auth;
    private readonly IAccountApi _accountApi;
    private readonly ISignInCoordinator _signIn;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<AccountViewModel> _logger;
    private CancellationTokenSource? _accountInfoCts;
    private bool _initialized;

    public AccountViewModel(
        IAuthService auth,
        IAccountApi accountApi,
        ISignInCoordinator signIn,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        ILogger<AccountViewModel> logger)
    {
        _auth = auth;
        _accountApi = accountApi;
        _signIn = signIn;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _logger = logger;

        // Every successful sign-in raises SignedIn, even when already signed in (account switch): always refresh.
        _auth.StatusChanged += (_, e) => _dispatcher.Run(() => ApplyStatus(e.Status, refreshAccount: true));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(IsExpired), nameof(IsSigningIn), nameof(ShowSignInButton), nameof(ShowAccount), nameof(ShowPersonGlyph), nameof(SignInButtonText), nameof(StatusTitle), nameof(StatusDetail), nameof(RailLabel))]
    public partial AuthStatus Status { get; set; } = AuthStatus.SignedOut;

    /// <summary>False until stored credentials were loaded, so the rail doesn't flash "Sign in".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignInButton), nameof(RailLabel))]
    public partial bool IsInitialized { get; set; }

    /// <summary>The sign-in window or cookie dialog is open. Auth itself never reports SigningIn.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSigningIn), nameof(ShowSignInButton), nameof(ShowAccount), nameof(ShowPersonGlyph), nameof(StatusTitle), nameof(RailLabel))]
    public partial bool IsSignInInProgress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignedInAsText), nameof(StatusTitle), nameof(RailLabel))]
    public partial string DisplayName { get; set; } = FallbackName;

    /// <summary>First word of the account name, for greetings. Null while signed out or before the name has loaded.</summary>
    [ObservableProperty]
    public partial string? FirstName { get; set; }

    [ObservableProperty]
    public partial string? Handle { get; set; }

    [ObservableProperty]
    public partial string? PhotoUrl { get; set; }

    public bool IsSignedIn => Status == AuthStatus.SignedIn;

    public bool IsExpired => Status == AuthStatus.Expired;

    public bool IsSigningIn => IsSignInInProgress || Status == AuthStatus.SigningIn;

    public bool ShowSignInButton => IsInitialized && !IsSigningIn && Status is AuthStatus.SignedOut or AuthStatus.Expired;

    /// <summary>The avatar button: signed in and no sign-in flow open.</summary>
    public bool ShowAccount => IsSignedIn && !IsSigningIn;

    /// <summary>Signed out, expired or not loaded yet: the rail shows a person glyph instead of the avatar.</summary>
    public bool ShowPersonGlyph => !ShowAccount && !IsSigningIn;

    public string SignInButtonText => Status == AuthStatus.Expired ? "Sign in again" : "Sign in";

    /// <summary>Label next to the rail account button.</summary>
    public string RailLabel => this switch
    {
        { IsSigningIn: true } => "Signing in…",
        { IsSignedIn: true } => DisplayName,
        { IsInitialized: false } => "Account",
        _ => SignInButtonText,
    };

    public string SignedInAsText => $"Signed in as {DisplayName}";

    public string StatusTitle => Status switch
    {
        AuthStatus.SignedIn => SignedInAsText,
        _ when IsSigningIn => "Signing in…",
        AuthStatus.Expired => "Your session expired",
        _ => "Not signed in",
    };

    public string StatusDetail => Status switch
    {
        AuthStatus.SignedIn => "Your library, playlists and likes sync with YouTube Music.",
        AuthStatus.Expired => "YouTube Music no longer accepts the saved session. Sign in again to see your library.",
        _ => "Sign in with your Google account to see your library, playlists and liked songs.",
    };

    /// <summary>Loads stored credentials (no network) and, when signed in, the account name and photo. Never throws.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        try
        {
            await _auth.InitializeAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restoring the saved sign-in failed");
            _notifications.ShowError("Could not restore your sign-in", ex);
        }

        // StatusChanged may already have applied this (and started the account fetch).
        ApplyStatus(_auth.Status, refreshAccount: _accountInfoCts is null);
        IsInitialized = true;
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SignInAsync() => RunSignInAsync(_signIn.SignInAsync);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SignInWithCookieHeaderAsync() => RunSignInAsync(_signIn.SignInWithCookieHeaderAsync);

    [RelayCommand]
    private async Task SignOutAsync()
    {
        try
        {
            await _signIn.SignOutAsync();
            ApplyStatus(_auth.Status, refreshAccount: false);
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Sign-out failed", ex);
        }
    }

    // Allows concurrent execution so a second click can bring the open sign-in window to the front.
    private async Task RunSignInAsync(Func<Task<bool>> flow)
    {
        if (IsSignInInProgress && !_signIn.IsSignInWindowOpen)
        {
            return;
        }

        var owner = !IsSignInInProgress;
        IsSignInInProgress = true;
        try
        {
            if (await flow() && owner)
            {
                _notifications.Show(new AppNotification(NotificationSeverity.Success, "Signed in", "Your YouTube Music account is connected."));
            }
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Sign-in failed", ex);
        }
        finally
        {
            if (owner)
            {
                IsSignInInProgress = false;
            }
        }
    }

    private void ApplyStatus(AuthStatus status, bool refreshAccount)
    {
        var previous = Status;
        Status = status;

        if (status == AuthStatus.SignedIn)
        {
            if (refreshAccount)
            {
                _ = LoadAccountInfoAsync();
            }

            return;
        }

        if (status == AuthStatus.SigningIn)
        {
            return;
        }

        _accountInfoCts?.Cancel();
        _accountInfoCts = null;
        DisplayName = FallbackName;
        FirstName = null;
        Handle = null;
        PhotoUrl = null;

        if (status == AuthStatus.Expired && previous != AuthStatus.Expired)
        {
            _notifications.Show(new AppNotification(
                NotificationSeverity.Warning,
                "Your YouTube Music session expired",
                "Sign in again to keep using your library, playlists and likes."));
        }
    }

    private async Task LoadAccountInfoAsync()
    {
        _accountInfoCts?.Cancel();
        var cts = new CancellationTokenSource();
        _accountInfoCts = cts;
        try
        {
            var info = await _accountApi.GetAccountInfoAsync(cts.Token);
            if (cts.IsCancellationRequested || Status != AuthStatus.SignedIn)
            {
                return;
            }

            DisplayName = string.IsNullOrWhiteSpace(info?.Name) ? FallbackName : info.Name;
            FirstName = info?.Name?.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            Handle = info?.ChannelHandle;
            PhotoUrl = info?.PhotoUrl;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load account info");
            _notifications.ShowError("Could not load your account details", ex);
        }
    }
}
