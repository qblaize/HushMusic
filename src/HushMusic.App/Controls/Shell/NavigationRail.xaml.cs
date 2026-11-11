using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using HushMusic.App.Helpers;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Controls.Shell;

/// <summary>
/// The left icon rail: navigation items, the pinned items (<see cref="SidebarPinsViewModel"/>), search, account and
/// settings. Selection follows <see cref="ShellViewModel.CurrentPage"/>.
/// </summary>
public sealed partial class NavigationRail : UserControl
{
    // Hovering a drag this long over Library or Playlists opens it ("spring-loaded"), so songs can be dropped on a playlist.
    private static readonly TimeSpan SpringDelay = TimeSpan.FromMilliseconds(650);

    private DispatcherQueueTimer? _springTimer;
    private RailButton? _springTarget;

    public NavigationRail()
    {
        InitializeComponent();
        foreach (var item in Items)
        {
            item.LabelRequested += OnItemLabelRequested;
        }

        // A label stays where its item was: hide it when the pins scroll or the rail changes size. A pin reached with
        // the keyboard (scrolled into view) gets its label back once the scrolling ends.
        PinScroller.ViewChanged += OnPinsScrolled;
        SizeChanged += (_, _) => LabelRequested?.Invoke(this, false);
    }

    private void OnPinsScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate && XamlRoot is { } root && FocusManager.GetFocusedElement(root) is RailButton { FocusState: FocusState.Keyboard } focused)
        {
            LabelRequested?.Invoke(focused, true);
            return;
        }

        LabelRequested?.Invoke(this, false);
    }

    /// <summary>A rail item wants its floating label shown (true) or hidden (false). Sender: the <see cref="RailButton"/>.</summary>
    public event EventHandler<bool>? LabelRequested;

    public ShellViewModel ViewModel { get; } = App.GetService<ShellViewModel>();

    /// <summary>The pinned items under the main ones.</summary>
    public SidebarPinsViewModel Pins { get; } = App.GetService<SidebarPinsViewModel>();

    private void OnItemLabelRequested(object? sender, bool show) => LabelRequested?.Invoke(sender, show);

    // In a short window the pins get what is left after the main items: when not even one fits, none show (no half cover).
    private void OnPinAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var pinHeight = MainItems.Children.OfType<RailButton>().FirstOrDefault()?.ActualHeight ?? 0;
        var divider = double.IsNaN(PinDivider.Height) ? 0 : PinDivider.Height;
        var fits = e.NewSize.Height >= divider + PinArea.RowSpacing + pinHeight;
        var visibility = fits ? Visibility.Visible : Visibility.Collapsed;
        PinDivider.Visibility = visibility;
        PinScroller.Visibility = visibility;
    }

    // Pins come and go: their labels are hooked up while they are on the rail.
    private void OnPinLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is RailButton button)
        {
            button.LabelRequested -= OnItemLabelRequested;
            button.LabelRequested += OnItemLabelRequested;
            if (button.IsSelected)
            {
                // Just pinned while its page is open (it was added at the end, maybe out of sight).
                button.StartBringIntoView();
            }

            if (TestPinMenu)
            {
                button.Click -= OnTestPinClick;
                button.Click += OnTestPinClick;
            }
        }
    }

    private void OnPinUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is RailButton button)
        {
            button.LabelRequested -= OnItemLabelRequested;
            button.Click -= OnTestPinClick;
            LabelRequested?.Invoke(button, false);
        }
    }

    // Automated test instances can't right-click: with HUSHMUSIC_TEST_PIN_MENU=1, invoking a pin also opens its menu.
    private static bool TestPinMenu { get; } =
        Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_BACKGROUND") == "1" && Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_PIN_MENU") == "1";

    private static void OnTestPinClick(object sender, RoutedEventArgs e)
    {
        if (sender is RailButton { ContextFlyout: { } menu } button)
        {
            menu.ShowAt(button);
        }
    }

    private void OnSpringDragOver(object sender, DragEventArgs e)
    {
        if (sender is not RailButton button || !TrackDragData.Has(e.DataView))
        {
            return;
        }

        // Nothing is dropped on the rail itself; it only opens the page.
        e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
        e.Handled = true;
        if (button.IsSelected || ReferenceEquals(button, _springTarget))
        {
            return;
        }

        _springTarget = button;
        _springTimer ??= CreateSpringTimer();
        _springTimer.Stop();
        _springTimer.Start();
    }

    private void OnSpringDragLeave(object sender, DragEventArgs e)
    {
        if (ReferenceEquals(sender, _springTarget))
        {
            _springTimer?.Stop();
            _springTarget = null;
        }
    }

    private DispatcherQueueTimer CreateSpringTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = SpringDelay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            var target = _springTarget;
            _springTarget = null;
            if (target?.Command is { } command && command.CanExecute(target.CommandParameter))
            {
                command.Execute(target.CommandParameter);
            }
        };
        return timer;
    }

    private IEnumerable<RailButton> Items =>
        MainItems.Children.OfType<RailButton>()
            .Concat(FooterItems.Children.OfType<RailButton>())
            .Append(Account.RailItem);
}
