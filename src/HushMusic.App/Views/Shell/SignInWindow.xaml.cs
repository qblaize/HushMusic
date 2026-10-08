using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using HushMusic.App.Services.Shell;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Views.Shell;

/// <summary>
/// The only place a WebView2 is used: Google sign-in. Logic lives in <see cref="SignInWindowViewModel"/>;
/// this class creates the browser, forwards window events and closes on request.
/// </summary>
public sealed partial class SignInWindow : Window
{
    private const int WidthDip = 520;
    private const int HeightDip = 720;

    private readonly IThemeService _theme = App.GetService<IThemeService>();
    private bool _closed;

    public SignInWindow(SignInWindowViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        ViewModel.CloseRequested += OnCloseRequested;
        RootGrid.Loaded += OnRootLoaded;
        Closed += OnClosed;

        ApplyTheme();
        _theme.ThemeChanged += OnThemeChanged;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
    }

    public SignInWindowViewModel ViewModel { get; }

    /// <summary>Sizes the window for the owner's DPI, centres it over the owner and shows it.</summary>
    public void ShowCentered(Window? owner)
    {
        var scale = owner?.Content?.XamlRoot?.RasterizationScale ?? 1.0;
        var size = new SizeInt32((int)(WidthDip * scale), (int)(HeightDip * scale));
        AppWindow.Resize(size);
        if (owner is not null)
        {
            var position = owner.AppWindow.Position;
            var ownerSize = owner.AppWindow.Size;
            AppWindow.Move(new PointInt32(
                position.X + Math.Max(0, (ownerSize.Width - size.Width) / 2),
                position.Y + Math.Max(0, (ownerSize.Height - size.Height) / 2)));
        }

        Activate();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    // Follows the app's theme setting: the window content, the system title bar and Google's page (where it supports it).
    private void ApplyTheme()
    {
        var theme = _theme.ActualTheme;
        RootGrid.RequestedTheme = theme;
        StyleTitleBar(AppWindow.TitleBar, theme);
        if (SignInView.CoreWebView2 is { } core)
        {
            core.Profile.PreferredColorScheme = theme == ElementTheme.Light
                ? CoreWebView2PreferredColorScheme.Light
                : CoreWebView2PreferredColorScheme.Dark;
        }
    }

    private static void StyleTitleBar(AppWindowTitleBar titleBar, ElementTheme theme)
    {
        Windows.UI.Color Get(string key, Windows.UI.Color fallback) => ThemeResources.GetColor(key, theme, fallback);
        var light = theme == ElementTheme.Light;

        titleBar.PreferredTheme = light ? TitleBarTheme.Light : TitleBarTheme.Dark;
        var background = Get("AppBackgroundColor", light ? Colors.White : ColorHelper.FromArgb(0xFF, 0x0B, 0x0B, 0x0C));
        var foreground = Get("TextSecondaryColor", ColorHelper.FromArgb(0xFF, 0xA1, 0xA1, 0xA6));
        var inactive = Get("TextTertiaryColor", ColorHelper.FromArgb(0xFF, 0x6E, 0x6E, 0x73));
        titleBar.BackgroundColor = background;
        titleBar.InactiveBackgroundColor = background;
        titleBar.ForegroundColor = foreground;
        titleBar.InactiveForegroundColor = inactive;
        titleBar.ButtonBackgroundColor = background;
        titleBar.ButtonInactiveBackgroundColor = background;
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactive;
        titleBar.ButtonHoverBackgroundColor = Get("Surface2Color", ColorHelper.FromArgb(0xFF, 0x1D, 0x1D, 0x20));
        titleBar.ButtonHoverForegroundColor = Get("TextPrimaryColor", ColorHelper.FromArgb(0xFF, 0xF5, 0xF5, 0xF7));
        titleBar.ButtonPressedBackgroundColor = Get("Surface1Color", ColorHelper.FromArgb(0xFF, 0x15, 0x15, 0x17));
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootLoaded;
        try
        {
            var environment = await ViewModel.CreateEnvironmentAsync();
            if (environment is null || _closed)
            {
                return;
            }

            await SignInView.EnsureCoreWebView2Async(environment);
            if (!_closed)
            {
                ApplyTheme();
                ViewModel.Attach(SignInView.CoreWebView2);
            }
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                ViewModel.OnBrowserFailed(ex);
            }
        }
    }

    // Deferred: the request can come from inside a WebView2 callback, which must not close its own host synchronously.
    private void OnCloseRequested(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        if (!_closed)
        {
            Close();
        }
    });

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _theme.ThemeChanged -= OnThemeChanged;
        ViewModel.CloseRequested -= OnCloseRequested;
        ViewModel.OnClosed();
        SignInView.Close();
    }
}
