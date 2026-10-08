using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using HushMusic.App.ViewModels.Shell;
using HushMusic.Core.Abstractions;
using VirtualKey = Windows.System.VirtualKey;

namespace HushMusic.App.Controls.TaskbarFlyout;

/// <summary>
/// The taskbar player's flyout: cover, title, seek bar, transport, like, volume and the next songs in the queue. Logic
/// lives in <see cref="PlayerViewModel"/> and <see cref="TaskbarFlyoutUpNextViewModel"/>; this is UI glue (seek drags,
/// Esc, focus, the open animation).
/// </summary>
public sealed partial class TaskbarFlyoutView : UserControl, IDisposable
{
    private Storyboard? _open;

    public TaskbarFlyoutView()
    {
        InitializeComponent();

        // Slider marks pointer events handled, so listen with handledEventsToo to know when a drag starts and ends.
        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => ViewModel.BeginSeek()), handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => ViewModel.EndSeek()), handledEventsToo: true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => ViewModel.EndSeek()), handledEventsToo: true);

        UpNext.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TaskbarFlyoutUpNextViewModel.HasItems))
            {
                PreferredHeightChanged?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    /// <summary>Esc was pressed.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>The content got taller or shorter (the "Up next" row appeared or went away): the window should refit.</summary>
    public event EventHandler? PreferredHeightChanged;

    public PlayerViewModel ViewModel { get; } = App.GetService<PlayerViewModel>();

    public TaskbarFlyoutUpNextViewModel UpNext { get; } = new(
        App.GetService<IQueueService>(),
        App.GetService<IPlayer>(),
        App.GetService<INotificationService>(),
        App.GetService<IUiDispatcher>());

    public void Dispose() => UpNext.Dispose();

    /// <summary>Fades and slides the content in (the window itself appears at once).</summary>
    public void PlayOpenAnimation()
    {
        _open?.Stop();
        Root.Opacity = 0;
        Slide.Y = 8;

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(140) };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var slide = new DoubleAnimation
        {
            From = 8,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(220),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 },
        };
        Storyboard.SetTarget(slide, Slide);
        Storyboard.SetTargetProperty(slide, "Y");

        _open = new Storyboard();
        _open.Children.Add(fade);
        _open.Children.Add(slide);
        _open.Completed += (_, _) =>
        {
            Root.Opacity = 1;
            Slide.Y = 0;
        };
        _open.Begin();
    }

    /// <summary>
    /// Test hook: clicks "Up next" tile <paramref name="position"/> (1-based) through its automation peer and returns its
    /// AutomationId and Name, or null when there is no such tile.
    /// </summary>
    internal string? InvokeUpNextTileForTest(int position)
    {
        if (UpNextTile(position - 1) is not { } tile || FrameworkElementAutomationPeer.CreatePeerForElement(tile) is not IInvokeProvider invoke)
        {
            return null;
        }

        var description = $"{AutomationProperties.GetAutomationId(tile)}: {AutomationProperties.GetName(tile)}";
        invoke.Invoke();
        return description;
    }

    /// <summary>Test hook: shows "Up next" tile <paramref name="position"/> (1-based) as hovered (screenshots can't hover).</summary>
    internal bool HoverUpNextTileForTest(int position) =>
        UpNextTile(position - 1) is { } tile && VisualStateManager.GoToState(tile, "PointerOver", useTransitions: false);

    private UpNextTileButton? UpNextTile(int index) =>
        UpNextList.ContainerFromIndex(index) is DependencyObject container && VisualTreeHelper.GetChildrenCount(container) > 0
            ? VisualTreeHelper.GetChild(container, 0) as UpNextTileButton
            : null;

    // The played tile leaves the row (it is the current song now), taking focus with it: keep focus in the flyout so
    // Esc and the keyboard still work.
    private void OnUpNextTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not UpNextTileButton tile || tile.FocusState == FocusState.Unfocused)
        {
            return;
        }

        var byKeyboard = tile.FocusState == FocusState.Keyboard;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (tile.FocusState != FocusState.Unfocused
                || (XamlRoot is { } root && FocusManager.GetFocusedElement(root) is UIElement focused && !ReferenceEquals(focused, tile)))
            {
                return;
            }

            if (byKeyboard && UpNextTile(0) is { } first)
            {
                first.Focus(FocusState.Keyboard);
            }
            else
            {
                Focus(FocusState.Programmatic);
            }
        });
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
