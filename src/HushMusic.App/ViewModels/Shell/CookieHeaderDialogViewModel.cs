using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.ViewModels.Shell;

/// <summary>Fallback sign-in: the user pastes the raw Cookie request header from a signed-in music.youtube.com tab.</summary>
public sealed partial class CookieHeaderDialogViewModel(IAuthService auth, ILogger<CookieHeaderDialogViewModel> logger) : ObservableObject
{
    private static readonly TimeSpan SubmitTimeout = TimeSpan.FromSeconds(30);

    public bool Succeeded { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    public partial string CookieHeader { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSubmit))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool CanSubmit => !IsBusy && !string.IsNullOrWhiteSpace(CookieHeader);

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Returns true when signed in (the dialog may close), false to keep it open with an error.</summary>
    public async Task<bool> SubmitAsync()
    {
        if (!CanSubmit)
        {
            return false;
        }

        var header = Normalize(CookieHeader);
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            using var timeout = new CancellationTokenSource(SubmitTimeout);
            await auth.SignInWithCookieHeaderAsync(header, timeout.Token);
            Succeeded = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "Signing in took too long. Check your connection and try again.";
            return false;
        }
        catch (Exception ex)
        {
            // Never log the header itself: it is a full Google session.
            logger.LogWarning(ex, "Cookie header sign-in failed");
            ErrorMessage = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Normalize(string value)
    {
        var header = value.Trim();
        if (header.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
        {
            header = header["cookie:".Length..].Trim();
        }

        // A pasted value can be wrapped over several lines; pairs are split on ';' so joining without a separator is safe.
        return string.Concat(header.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
