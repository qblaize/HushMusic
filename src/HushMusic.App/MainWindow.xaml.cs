using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using HushMusic.App.Controls.MiniPlayer;
using HushMusic.App.Services.Shell;

namespace HushMusic.App;

/// <summary>
/// The app window. Hosts the shell and, in mini-player mode, the mini player on top of the collapsed shell (pages,
/// scroll positions and state survive). Window modes, taskbar and tray are run by <see cref="WindowModeService"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    private MiniPlayerView? _mini;
    private Control? _focusBeforeMini;

    public MainWindow()
    {
        InitializeComponent();

        if (DesignSystems.IsWindowsActive)
        {
            // Windows design: Mica, showing through wherever the content above is transparent.
            SystemBackdrop = new MicaBackdrop();
            RootGrid.Background = null;
        }

        ExtendsContentIntoTitleBar = true;
        AppWindow.Resize(new SizeInt32(1280, 840));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        ConfigurePresenter();

        App.GetService<IThemeService>().AttachWindow(this);
        SetTitleBar(ShellView.TitleBarElement);
        ShellView.TitleBarLayoutChanged += (_, _) => UpdatePassthroughRegions();

        // An open sign-in window would otherwise keep the process alive after the app's services are disposed.
        Closed += (_, _) => App.GetService<ISignInCoordinator>().CloseSignInWindow();

        Modes = (WindowModeService)App.GetService<IWindowModeService>();
        Modes.Attach(this);
    }

    internal WindowModeService Modes { get; }

    /// <summary>The mini player, once it has been shown.</summary>
    internal MiniPlayerView? MiniPlayer => _mini;

    private bool IsMiniShown => _mini is { Visibility: Visibility.Visible };

    /// <summary>Minimum size of the normal (overlapped) window.</summary>
    internal void ConfigurePresenter()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 960;
            presenter.PreferredMinimumHeight = 640;
        }
    }

    /// <summary>Swaps the visible content between the shell and the mini player (the presenter is the service's job).</summary>
    internal void ShowMiniPlayerContent(bool mini)
    {
        if (mini)
        {
            if (_mini is null)
            {
                _mini = new MiniPlayerView { Visibility = Visibility.Collapsed };
                _mini.ExpandRequested += (_, _) => Modes.ExitMiniPlayer();
                _mini.CloseRequested += (_, _) => Modes.CloseLikeUser();
                _mini.InteractiveLayoutChanged += (_, _) => UpdatePassthroughRegions();
                RootGrid.Children.Add(_mini);
            }

            _focusBeforeMini = ShellView.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as Control : null;
            _mini.Visibility = Visibility.Visible;
            ShellView.Visibility = Visibility.Collapsed;
            SetTitleBar(_mini.DragRegionElement);
            _mini.Activate(TimeSpan.FromSeconds(2.5));
        }
        else if (_mini is not null)
        {
            _mini.Deactivate();
            ShellView.Visibility = Visibility.Visible;
            _mini.Visibility = Visibility.Collapsed;
            SetTitleBar(ShellView.TitleBarElement);
            _focusBeforeMini?.Focus(FocusState.Programmatic);
            _focusBeforeMini = null;
        }

        // After the swap has been laid out.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, UpdatePassthroughRegions);
    }

    // SetTitleBar makes the whole band (or the whole mini player) a drag area; buttons inside need to stay clickable.
    private void UpdatePassthroughRegions()
    {
        var mini = IsMiniShown;
        FrameworkElement host = mini ? _mini! : ShellView;
        if (host.XamlRoot is not { } root)
        {
            return;
        }

        var scale = root.RasterizationScale;
        var rects = new List<RectInt32>();
        foreach (var element in mini ? _mini!.InteractiveElements : ShellView.TitleBarInteractiveElements)
        {
            if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0)
            {
                continue;
            }

            var bounds = element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
            rects.Add(new RectInt32(
                (int)Math.Round(bounds.X * scale),
                (int)Math.Round(bounds.Y * scale),
                (int)Math.Round(bounds.Width * scale),
                (int)Math.Round(bounds.Height * scale)));
        }

        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, [.. rects]);
    }
}
