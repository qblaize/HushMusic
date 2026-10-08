using System.ComponentModel;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;
using HushMusic.App.Controls.Shell;
using HushMusic.App.Services.Shell;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Views;

/// <summary>
/// Window content: icon rail, title band, page frame, player bar (floating or docked), Now Playing, toasts and the
/// search modal. Code-behind is UI glue only: shortcuts, rail labels, the player bar for the layout, and hiding the shell
/// while Now Playing covers it.
/// </summary>
public sealed partial class ShellPage : Page
{
    private static readonly TimeSpan TipFade = TimeSpan.FromMilliseconds(70);

    private bool _spaceHandled;

    public ShellPage()
    {
        InitializeComponent();
        App.GetService<INavigationService>().Initialize(ContentFrame);
        Rail.LabelRequested += OnRailLabelRequested;
        RailTip.OpacityTransition = new ScalarTransition { Duration = TipFade };
        BackButton.SizeChanged += (_, _) => TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
        foreach (var button in NowPlaying.TitleBarButtons)
        {
            button.SizeChanged += (_, _) => TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
            button.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty));
        }

        NowPlaying.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty));
        NowPlaying.CoverChanged += (_, covered) => SetCoveredByNowPlaying(covered);
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnPointerPressed), handledEventsToo: true);
        Loaded += OnLoaded;
        Loaded += (_, _) =>
        {
            ViewModel.Player.PropertyChanged -= OnPlayerPropertyChanged;
            ViewModel.Player.PropertyChanged += OnPlayerPropertyChanged;
            ApplyPlayerLayout();
        };
        Unloaded += (_, _) => ViewModel.Player.PropertyChanged -= OnPlayerPropertyChanged;
    }

    /// <summary>Interactive elements inside the title band changed size or position (the window updates its pass-through regions).</summary>
    public event EventHandler? TitleBarLayoutChanged;

    public ShellViewModel ViewModel { get; } = App.GetService<ShellViewModel>();

    /// <summary>The draggable title band, for <see cref="Window.SetTitleBar"/>.</summary>
    public UIElement TitleBarElement => DragRegion;

    /// <summary>Controls inside the title band that must stay clickable.</summary>
    public IReadOnlyList<FrameworkElement> TitleBarInteractiveElements =>
        NowPlaying.Visibility == Visibility.Visible ? [BackButton, .. NowPlaying.TitleBarButtons] : [BackButton];

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        // Note: ThemeShadow.Receivers.Add crashes the process (CsWinRT UIElementWeakCollection marshalling), so the
        // shared shadow is used without receivers.
        XamlRoot.Changed += (_, _) => TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
        TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);

        ViewModel.Navigate(PageKey.Home);
        await ViewModel.InitializeAsync();
    }

    // ===== Keyboard and mouse shortcuts =====

    // Tunnelling handler so Space reaches play/pause before a mouse-focused button can "click" it.
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var handled = ViewModel.HandleShortcut(
            e.Key,
            ShortcutFocusPolicy.CurrentModifiers(),
            ShortcutFocusPolicy.Classify(XamlRoot),
            e.KeyStatus.WasKeyDown);
        e.Handled = handled;
        if (e.Key == VirtualKey.Space)
        {
            _spaceHandled = handled;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        // Buttons activate on Space key-up; swallow it when the key-down was ours.
        if (e.Key == VirtualKey.Space && _spaceHandled)
        {
            _spaceHandled = false;
            e.Handled = true;
        }
    }

    // Mouse "back" button: closes Now Playing first.
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ViewModel.Search.IsOpen
            && e.GetCurrentPoint(this).Properties.IsXButton1Pressed
            && (ViewModel.NowPlaying.IsOpen || ViewModel.CanGoBack))
        {
            ViewModel.Back();
            e.Handled = true;
        }
    }

    // While Now Playing covers the pages, they are collapsed: nothing to draw behind the drifting artwork, and Tab /
    // UI Automation only see the rail and Now Playing. They come back before the sheet starts to close.
    private void SetCoveredByNowPlaying(bool covered)
    {
        var visibility = covered ? Visibility.Collapsed : Visibility.Visible;
        BackButton.Visibility = visibility;
        ContentArea.Visibility = visibility;
        if (covered)
        {
            RailTip.Opacity = 0;
        }

        TitleBarLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    // ===== Player layout =====

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.IsMinimalLayout))
        {
            ApplyPlayerLayout();
        }
    }

    // The standard bar follows the layout by binding. The minimal one is created on first use, then only shown or
    // hidden: switching back and forth never builds a second one.
    private void ApplyPlayerLayout()
    {
        var minimal = ViewModel.Player.IsMinimalLayout;
        if (minimal && MinimalBar is null)
        {
            FindName(nameof(MinimalBar));
        }

        if (MinimalBar is not null)
        {
            MinimalBar.Visibility = minimal ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ===== Rail labels: shown at once, to the right of the item, vertically centred =====

    private void OnRailLabelRequested(object? sender, bool show)
    {
        if (!show || sender is not RailButton button || string.IsNullOrEmpty(button.Label))
        {
            RailTip.Opacity = 0;
            return;
        }

        RailTipText.Text = button.Label;
        RailTipShortcut.Text = button.Shortcut;
        RailTipShortcut.Visibility = string.IsNullOrEmpty(button.Shortcut) ? Visibility.Collapsed : Visibility.Visible;
        RailTip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var anchor = button.TransformToVisual(TipLayer).TransformPoint(new Point(button.ActualWidth, button.ActualHeight / 2));
        Canvas.SetLeft(RailTip, Math.Round(anchor.X + 10));
        Canvas.SetTop(RailTip, Math.Round(anchor.Y - (RailTip.DesiredSize.Height / 2)));
        RailTip.Opacity = 1;
    }
}
