using Microsoft.Extensions.Logging;
using HushMusic.App.Dialogs.Shell;
using HushMusic.App.ViewModels.Shell;
using HushMusic.App.Views.Shell;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.Services.Shell;

/// <summary>
/// Runs the sign-in flows: the WebView2 Google sign-in window (cookie capture) and the
/// "paste cookie header" fallback dialog. Also owns the throwaway WebView2 profile folder.
/// Call from the UI thread.
/// </summary>
public interface ISignInCoordinator
{
    bool IsSignInWindowOpen { get; }

    /// <summary>Opens (or focuses) the sign-in window. Completes when it closes; true if the user is now signed in.</summary>
    Task<bool> SignInAsync();

    /// <summary>Shows the fallback dialog. True if the pasted cookie header signed the user in.</summary>
    Task<bool> SignInWithCookieHeaderAsync();

    /// <summary>Signs out (wipes stored credentials) and deletes the WebView2 profile folder.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);

    /// <summary>Deletes the WebView2 profile folder if it exists (retrying briefly while the browser process lets go).</summary>
    Task DeleteBrowserProfileAsync();

    /// <summary>The main window is closing: close the sign-in window and abandon the flow.</summary>
    void CloseSignInWindow();
}

public sealed class SignInCoordinator(
    IAuthService auth,
    IAppPaths paths,
    IDialogService dialogs,
    INotificationService notifications,
    ILoggerFactory loggerFactory) : ISignInCoordinator
{
    private static readonly TimeSpan BrowserExitTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger<SignInCoordinator> _logger = loggerFactory.CreateLogger<SignInCoordinator>();
    private SignInWindow? _window;
    private Task<bool>? _windowTask;
    private bool _dialogOpen;
    private bool _appClosing;

    public bool IsSignInWindowOpen => _window is not null;

    private string ProfileFolder => Path.Combine(paths.Root, "webview-signin");

    public Task<bool> SignInAsync()
    {
        if (_windowTask is not null)
        {
            _window?.Activate();
            return _windowTask;
        }

        _windowTask = RunSignInWindowAsync();
        return _windowTask;
    }

    public async Task<bool> SignInWithCookieHeaderAsync()
    {
        if (_dialogOpen)
        {
            return false;
        }

        _dialogOpen = true;
        try
        {
            var viewModel = new CookieHeaderDialogViewModel(auth, loggerFactory.CreateLogger<CookieHeaderDialogViewModel>());
            await dialogs.ShowAsync(new CookieHeaderDialog(viewModel));
            return viewModel.Succeeded;
        }
        catch (Exception ex)
        {
            notifications.ShowError("Could not open the cookie sign-in dialog", ex);
            return false;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        _window?.Close();
        await auth.SignOutAsync(cancellationToken);
        await DeleteBrowserProfileAsync();
    }

    public Task DeleteBrowserProfileAsync()
    {
        var folder = ProfileFolder;
        return Task.Run(async () =>
        {
            for (var attempt = 1; Directory.Exists(folder); attempt++)
            {
                try
                {
                    Directory.Delete(folder, recursive: true);
                    _logger.LogInformation("Deleted the sign-in browser profile");
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt >= 10)
                    {
                        _logger.LogWarning(ex, "Could not delete the sign-in browser profile at {Folder}; it will be retried on the next start", folder);
                        return;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(400)).ConfigureAwait(false);
                }
            }
        });
    }

    public void CloseSignInWindow()
    {
        if (_window is { } window)
        {
            _appClosing = true;
            window.Close();
        }
    }

    private async Task<bool> RunSignInWindowAsync()
    {
        SignInWindowViewModel? viewModel = null;
        try
        {
            // Never reuse a previous profile: every sign-in starts from a clean browser.
            await DeleteBrowserProfileAsync();

            viewModel = new SignInWindowViewModel(auth, ProfileFolder, loggerFactory.CreateLogger<SignInWindowViewModel>());
            var window = new SignInWindow(viewModel);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.Closed += (_, _) => closed.TrySetResult();
            _window = window;
            window.ShowCentered(App.MainWindow);

            await closed.Task;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sign-in window failed");
            notifications.ShowError("Could not open the sign-in window", ex);
            _window?.Close();
        }
        finally
        {
            _window = null;
        }

        try
        {
            if (viewModel is not null)
            {
                // Wait for msedgewebview2.exe to release the profile, then remove it so no browser session is left on disk.
                await Task.WhenAny(viewModel.BrowserExited, Task.Delay(BrowserExitTimeout));
                await DeleteBrowserProfileAsync();
            }

            if (_appClosing)
            {
                return false;
            }

            App.MainWindow?.Activate();
            if (viewModel?.CookieHeaderRequested == true)
            {
                return await SignInWithCookieHeaderAsync();
            }

            return viewModel?.Succeeded == true;
        }
        finally
        {
            _windowTask = null;
        }
    }
}
