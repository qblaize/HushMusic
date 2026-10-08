using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Services.Pages;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>
/// Base for content page view models: navigation lifetime, the shared item actions and the
/// loading / error / empty / sign-in states every page shows.
/// </summary>
public abstract partial class PageViewModelBase : ViewModelBase, INavigationAware
{
    private static readonly PropertyChangedEventArgs ShowLoadingArgs = new(nameof(ShowLoading));
    private static readonly PropertyChangedEventArgs ShowErrorArgs = new(nameof(ShowError));
    private static readonly PropertyChangedEventArgs ShowBusyBarArgs = new(nameof(ShowBusyBar));

    private CancellationTokenSource _navigationCts = new();

    protected PageViewModelBase(PageServices services)
        : base(services.Notifications)
    {
        Services = services;
    }

    /// <summary>True once the page has something to show (the first load succeeded).</summary>
    [ObservableProperty]
    public partial bool HasContent { get; set; }

    /// <summary>Loaded, but there is nothing in it.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool RequiresSignIn { get; set; }

    public bool ShowLoading => IsBusy && !HasContent && !RequiresSignIn;

    public bool ShowError => !IsBusy && !HasContent && !RequiresSignIn && ErrorMessage is not null;

    /// <summary>A reload while the previous content stays on screen (refresh, tab switch).</summary>
    public bool ShowBusyBar => IsBusy && HasContent;

    protected PageServices Services { get; }

    protected IMediaItemActions Actions => Services.Actions;

    /// <summary>Cancelled when the user leaves the page; use it for work outside <see cref="ViewModelBase.RunAsync"/>.</summary>
    protected CancellationToken NavigationToken => _navigationCts.Token;

    /// <summary>True while handling a Back navigation (the page was already showing this content).</summary>
    protected bool IsBackNavigation { get; private set; }

    /// <summary>For cached pages that treat Back differently from a fresh visit.</summary>
    public Task OnNavigatedToAsync(object? parameter, bool isBackNavigation)
    {
        IsBackNavigation = isBackNavigation;
        return OnNavigatedToAsync(parameter);
    }

    public Task OnNavigatedToAsync(object? parameter)
    {
        if (_navigationCts.IsCancellationRequested)
        {
            _navigationCts.Dispose();
            _navigationCts = new CancellationTokenSource();
        }

        return OnNavigatedToCoreAsync(parameter);
    }

    public void OnNavigatedFrom()
    {
        CancelPendingWork();
        _navigationCts.Cancel();
        OnNavigatedFromCore();
    }

    protected abstract Task OnNavigatedToCoreAsync(object? parameter);

    protected virtual void OnNavigatedFromCore()
    {
    }

    /// <summary>(Re)loads the page. Used by the Retry button of the error state.</summary>
    protected abstract Task LoadAsync();

    /// <summary>Reports a failure that happened outside <see cref="ViewModelBase.RunAsync"/> (load more, actions).</summary>
    protected void ReportError(string title, Exception exception)
    {
        if (exception is OperationCanceledException && NavigationToken.IsCancellationRequested)
        {
            return;
        }

        Notifications.ShowError(title, exception);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsBusy) or nameof(ErrorMessage) or nameof(HasContent) or nameof(RequiresSignIn))
        {
            base.OnPropertyChanged(ShowLoadingArgs);
            base.OnPropertyChanged(ShowErrorArgs);
            base.OnPropertyChanged(ShowBusyBarArgs);
        }
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    [RelayCommand]
    private void OpenItem(object? item) => Actions.Open(item);
}
