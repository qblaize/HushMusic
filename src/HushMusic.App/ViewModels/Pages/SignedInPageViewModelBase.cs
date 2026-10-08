using HushMusic.App.Services.Pages;
using HushMusic.Core;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>
/// Base for pages backed by the signed-in user's library. Signed out (or expired) shows the sign-in
/// empty state instead of an error, and the page reloads by itself once the user signs in.
/// </summary>
public abstract partial class SignedInPageViewModelBase : PageViewModelBase
{
    protected SignedInPageViewModelBase(PageServices services)
        : base(services)
    {
    }

    protected abstract string LoadErrorTitle { get; }

    protected override Task OnNavigatedToCoreAsync(object? parameter)
    {
        Services.Auth.StatusChanged += OnAuthStatusChanged;
        return LoadAsync();
    }

    protected override void OnNavigatedFromCore() => Services.Auth.StatusChanged -= OnAuthStatusChanged;

    protected sealed override Task LoadAsync()
    {
        if (Services.Auth.Status != AuthStatus.SignedIn)
        {
            ShowSignIn();
            return Task.CompletedTask;
        }

        RequiresSignIn = false;
        return RunAsync(
            async ct =>
            {
                try
                {
                    await LoadContentAsync(ct);
                    HasContent = true;
                }
                catch (AuthRequiredException)
                {
                    ShowSignIn();
                }
            },
            LoadErrorTitle);
    }

    /// <summary>Loads the first page of data. Throwing <see cref="AuthRequiredException"/> shows the sign-in state.</summary>
    protected abstract Task LoadContentAsync(CancellationToken cancellationToken);

    protected abstract void ClearContent();

    /// <summary>Error callback for incremental loads: auth failures switch to the sign-in state.</summary>
    protected void HandleLoadMoreError(Exception exception)
    {
        if (exception is AuthRequiredException)
        {
            ShowSignIn();
        }
        else
        {
            ReportError("Couldn't load more", exception);
        }
    }

    private void ShowSignIn()
    {
        ClearContent();
        HasContent = false;
        IsEmpty = false;
        RequiresSignIn = true;
    }

    private void OnAuthStatusChanged(object? sender, AuthStatusChangedEventArgs e) =>
        Services.Dispatcher.Run(() =>
        {
            switch (e.Status)
            {
                case AuthStatus.SignedIn:
                    _ = LoadAsync();
                    break;
                case AuthStatus.SignedOut:
                case AuthStatus.Expired:
                    CancelPendingWork();
                    ShowSignIn();
                    break;
            }
        });
}
