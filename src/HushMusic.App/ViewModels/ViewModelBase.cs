using CommunityToolkit.Mvvm.ComponentModel;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.ViewModels;

/// <summary>Implemented by page view models; the page forwards OnNavigatedTo/From.</summary>
public interface INavigationAware
{
    Task OnNavigatedToAsync(object? parameter);

    void OnNavigatedFrom();
}

public abstract partial class ViewModelBase(INotificationService notifications) : ObservableObject
{
    private CancellationTokenSource? _cts;

    protected INotificationService Notifications { get; } = notifications;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    /// <summary>
    /// Runs <paramref name="work"/> with busy state, cancellation of any previous run and error reporting
    /// (InfoBar + <see cref="ErrorMessage"/>). Never throws.
    /// </summary>
    protected async Task RunAsync(Func<CancellationToken, Task> work, string errorTitle)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await work(cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            Notifications.ShowError(errorTitle, ex);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsBusy = false;
            }
        }
    }

    /// <summary>Cancels the work started by <see cref="RunAsync"/>, e.g. when leaving the page.</summary>
    protected void CancelPendingWork() => _cts?.Cancel();
}
