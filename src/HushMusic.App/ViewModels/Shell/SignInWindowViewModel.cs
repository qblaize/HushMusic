using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.ViewModels.Shell;

public enum SignInStage
{
    Starting,
    WaitingForUser,
    Finishing,
    Failed,
    Completed,
}

/// <summary>
/// Drives the WebView2 Google sign-in window: loads Google's login, and as soon as the browser lands on
/// music.youtube.com captures the session cookies, hands them to <see cref="IAuthService"/> and asks to close.
/// music.youtube.com itself is never shown: an overlay covers it from the moment navigation starts.
/// </summary>
public sealed partial class SignInWindowViewModel : ObservableObject
{
    // Same entry point the YouTube Music web app uses for "Sign in".
    public const string SignInUrl = "https://accounts.google.com/ServiceLogin?ltmpl=music&service=youtube&passive=true&continue=https%3A%2F%2Fmusic.youtube.com%2F";

    private const string MusicHost = "music.youtube.com";
    private const string MusicOrigin = "https://music.youtube.com";
    private const int CookieAttempts = 6;

    // ytmusicapi derives SAPISIDHASH from __Secure-3PAPISID, falling back to SAPISID.
    private static readonly string[] SessionCookieNames = ["__Secure-3PAPISID", "SAPISID"];

    private readonly IAuthService _auth;
    private readonly ILogger<SignInWindowViewModel> _logger;
    private readonly CancellationTokenSource _closing = new();
    private readonly TaskCompletionSource _browserExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CoreWebView2? _core;
    private bool _capturing;

    public SignInWindowViewModel(IAuthService auth, string userDataFolder, ILogger<SignInWindowViewModel> logger)
    {
        _auth = auth;
        _logger = logger;
        UserDataFolder = userDataFolder;
    }

    /// <summary>Raised when the window should close (signed in, cancelled, or switching to the cookie dialog).</summary>
    public event EventHandler? CloseRequested;

    public string UserDataFolder { get; }

    public bool Succeeded { get; private set; }

    public bool CookieHeaderRequested { get; private set; }

    /// <summary>Completes when the WebView2 browser process has exited and released the profile folder.</summary>
    public Task BrowserExited => _browserExited.Task;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOverlayVisible), nameof(IsBusy), nameof(CanRetry))]
    public partial SignInStage Stage { get; set; } = SignInStage.Starting;

    [ObservableProperty]
    public partial string OverlayTitle { get; set; } = "Opening Google sign-in…";

    [ObservableProperty]
    public partial string OverlayMessage { get; set; } = string.Empty;

    public bool IsOverlayVisible => Stage != SignInStage.WaitingForUser;

    public bool IsBusy => Stage is SignInStage.Starting or SignInStage.Finishing or SignInStage.Completed;

    public bool CanRetry => Stage == SignInStage.Failed && _core is not null;

    /// <summary>Creates the browser environment with its own throwaway profile folder. Null on failure (already reported in the overlay).</summary>
    public async Task<CoreWebView2Environment?> CreateEnvironmentAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(string.Empty, UserDataFolder, new CoreWebView2EnvironmentOptions());
            environment.BrowserProcessExited += (_, _) => _browserExited.TrySetResult();
            return environment;
        }
        catch (Exception ex)
        {
            _browserExited.TrySetResult();
            OnBrowserFailed(ex);
            return null;
        }
    }

    /// <summary>Called once the WebView2 is ready. Locks the browser down and starts the Google sign-in.</summary>
    public void Attach(CoreWebView2 core)
    {
        if (_closing.IsCancellationRequested)
        {
            return;
        }

        _core = core;
        var settings = core.Settings;
        settings.AreDevToolsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.ProcessFailed += OnProcessFailed;
        core.Navigate(SignInUrl);
    }

    public void OnBrowserFailed(Exception exception)
    {
        _logger.LogError(exception, "The sign-in browser could not start");
        Fail(
            "The sign-in browser could not start",
            "Microsoft Edge WebView2 Runtime may be missing or broken. You can sign in by pasting a cookie header instead.");
    }

    /// <summary>The window closed (by the user or after success): stop everything.</summary>
    public void OnClosed()
    {
        _closing.Cancel();
        if (_core is { } core)
        {
            try
            {
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.ProcessFailed -= OnProcessFailed;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Sign-in browser was already gone");
            }
        }

        _core = null;
    }

    [RelayCommand]
    private void Retry()
    {
        if (_core is null)
        {
            return;
        }

        Stage = SignInStage.Starting;
        OverlayTitle = "Opening Google sign-in…";
        OverlayMessage = string.Empty;
        _core.Navigate(SignInUrl);
    }

    [RelayCommand]
    private void UseCookieHeader()
    {
        CookieHeaderRequested = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private static bool IsMusicHost(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && string.Equals(uri.Host, MusicHost, StringComparison.OrdinalIgnoreCase);

    // Domain is passed through untouched (leading dot included). WebView2 reports session cookies with Expires = -1.
    private static BrowserCookie ToBrowserCookie(CoreWebView2Cookie cookie) => new(
        cookie.Name,
        cookie.Value,
        cookie.Domain,
        cookie.Path,
        cookie.IsSession || cookie.Expires <= 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds((long)(cookie.Expires * 1000)),
        cookie.IsSecure,
        cookie.IsHttpOnly);

    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (IsMusicHost(args.Uri) && Stage != SignInStage.Completed)
        {
            // Cover the page before it renders: the user must never see music.youtube.com in this window.
            Stage = SignInStage.Finishing;
            OverlayTitle = "Finishing sign-in…";
            OverlayMessage = "Saving your YouTube Music session.";
        }
    }

    private async void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
    {
        try
        {
            var source = sender.Source;
            if (IsMusicHost(source))
            {
                await CaptureSessionAsync(sender);
                return;
            }

            if (source.StartsWith("about:", StringComparison.OrdinalIgnoreCase) || Stage is SignInStage.Failed or SignInStage.Completed)
            {
                return;
            }

            if (!args.IsSuccess)
            {
                if (args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
                {
                    _logger.LogWarning("Sign-in page failed to load: {Status}", args.WebErrorStatus);
                    Fail("Couldn't load Google sign-in", $"Check your internet connection and try again ({args.WebErrorStatus}).");
                }

                return;
            }

            Stage = SignInStage.WaitingForUser;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign-in navigation handling failed");
            Fail("Sign-in failed", ex.Message);
        }
    }

    private async Task CaptureSessionAsync(CoreWebView2 core)
    {
        if (_capturing || Stage == SignInStage.Completed)
        {
            return;
        }

        _capturing = true;
        try
        {
            var token = _closing.Token;
            IReadOnlyList<CoreWebView2Cookie> cookies = [];
            for (var attempt = 0; attempt < CookieAttempts; attempt++)
            {
                cookies = await core.CookieManager.GetCookiesAsync(MusicOrigin);
                if (cookies.Any(c => SessionCookieNames.Contains(c.Name)))
                {
                    break;
                }

                // Google sometimes sets the session cookies a moment after the redirect lands.
                await Task.Delay(TimeSpan.FromMilliseconds(500), token);
            }

            token.ThrowIfCancellationRequested();

            // Stop music.youtube.com from running (and playing anything) behind the overlay.
            core.Navigate("about:blank");

            if (!cookies.Any(c => SessionCookieNames.Contains(c.Name)))
            {
                _logger.LogWarning("Landed on YouTube Music without a session cookie ({Count} cookies)", cookies.Count);
                Fail(
                    "Google didn't finish signing you in",
                    "Try again. If it keeps happening, sign in by pasting a cookie header instead.");
                return;
            }

            _logger.LogInformation("Captured {Count} YouTube Music cookies from the sign-in window", cookies.Count);
            await _auth.SignInWithCookiesAsync([.. cookies.Select(ToBrowserCookie)], token);

            Succeeded = true;
            Stage = SignInStage.Completed;
            OverlayTitle = "Signed in";
            OverlayMessage = string.Empty;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cookie sign-in failed");
            Fail("YouTube Music didn't accept the sign-in", ex.Message);
        }
        finally
        {
            _capturing = false;
        }
    }

    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        // Keep Google's pop-ups ("Learn more", "Help") inside this window instead of spawning browser windows.
        args.Handled = true;
        if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            sender.Navigate(uri.AbsoluteUri);
        }
    }

    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
    {
        _logger.LogError("Sign-in browser process failed: {Kind}", args.ProcessFailedKind);
        Fail("The sign-in browser stopped working", "Close this window and try again.");
    }

    private void Fail(string title, string message)
    {
        if (_closing.IsCancellationRequested)
        {
            return;
        }

        OverlayTitle = title;
        OverlayMessage = message;
        Stage = SignInStage.Failed;
        OnPropertyChanged(nameof(CanRetry));
    }
}
