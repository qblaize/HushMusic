using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Services.Pages;
using HushMusic.App.ViewModels.Shell;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

public sealed partial class HomeViewModel : PageViewModelBase
{
    // Going Back to Home keeps the shelves (and scroll position) unless they are older than this.
    private static readonly TimeSpan MaxAgeOnBack = TimeSpan.FromMinutes(10);


    private readonly IBrowseApi _browse;
    private readonly AccountViewModel _account;
    private DateTimeOffset _loadedAt;
    private bool _accountChanged;
    private bool _isActive;

    public HomeViewModel(IBrowseApi browse, AccountViewModel account, PageServices services)
        : base(services)
    {
        _browse = browse;
        _account = account;
        _account.PropertyChanged += OnAccountPropertyChanged;
        UpdateGreeting();
        Shelves = new IncrementalCollection<Shelf>(
            (continuation, ct) => _browse.GetHomeAsync(continuation, ct),
            ex => ReportError("Couldn't load more of your home feed", ex),
            () => NavigationToken);

        // The feed is personal: signing in or out (or the session expiring) makes the loaded one wrong.
        services.Auth.StatusChanged += (_, _) => services.Dispatcher.Run(() =>
        {
            _accountChanged = true;
            if (_isActive)
            {
                _ = LoadAsync();
            }
        });
    }

    public IncrementalCollection<Shelf> Shelves { get; }

    /// <summary>"Good evening, Alex" (no name when signed out).</summary>
    [ObservableProperty]
    public partial string Greeting { get; set; } = string.Empty;

    /// <summary>Today's date above the greeting, uppercase ("WEDNESDAY, 7 OCTOBER").</summary>
    [ObservableProperty]
    public partial string DateText { get; set; } = string.Empty;

    /// <summary>The page should scroll to the top: this visit shows a fresh feed.</summary>
    public bool IsFreshVisit { get; private set; }

    // Every visit from start-up or the nav pane gets a fresh feed; Back keeps what was on screen.
    protected override Task OnNavigatedToCoreAsync(object? parameter)
    {
        _isActive = true;
        UpdateGreeting();
        IsFreshVisit = !HasContent || !IsBackNavigation || _accountChanged || DateTimeOffset.UtcNow - _loadedAt > MaxAgeOnBack;
        return IsFreshVisit ? LoadAsync() : Task.CompletedTask;
    }

    protected override void OnNavigatedFromCore() => _isActive = false;

    protected override Task LoadAsync() => RunAsync(
        async ct =>
        {
            var page = await _browse.GetHomeAsync(null, ct);
            Shelves.Reset(page.Items, page.Continuation);
            _loadedAt = DateTimeOffset.UtcNow;
            _accountChanged = false;
            HasContent = true;
            IsEmpty = Shelves.Count == 0;
        },
        "Couldn't load your home feed");

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    /// <summary>Called by the page when the user scrolls near the bottom.</summary>
    [RelayCommand]
    private Task LoadMoreAsync() => IsBusy || !HasContent ? Task.CompletedTask : Shelves.LoadMoreAsync();

    /// <summary>05–12 morning, 12–18 afternoon, otherwise evening.</summary>
    internal static string GreetingFor(int hour, string? firstName)
    {
        var greeting = hour switch
        {
            >= 5 and < 12 => "Good morning",
            >= 12 and < 18 => "Good afternoon",
            _ => "Good evening",
        };
        return string.IsNullOrWhiteSpace(firstName) ? greeting : $"{greeting}, {firstName}";
    }

    private void UpdateGreeting()
    {
        var now = DateTime.Now;
        Greeting = GreetingFor(now.Hour, FirstName());
        DateText = now.ToString("dddd, d MMMM", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture);
    }

    private string? FirstName() => _account.IsSignedIn ? _account.FirstName : null;

    private void OnAccountPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountViewModel.FirstName) or nameof(AccountViewModel.IsSignedIn))
        {
            Services.Dispatcher.Run(UpdateGreeting);
        }
    }
}
