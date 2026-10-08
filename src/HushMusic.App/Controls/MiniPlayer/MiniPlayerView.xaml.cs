using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using HushMusic.App.Services.Shell;
using HushMusic.App.Services.Windowing;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Controls.MiniPlayer;

/// <summary>
/// The mini player shown in the always-on-top compact window. Logic lives in <see cref="PlayerViewModel"/>; this is
/// UI glue: hover reveal, keyboard shortcuts and the pieces the window needs (drag region, pass-through buttons).
/// </summary>
public sealed partial class MiniPlayerView : UserControl
{
    private static readonly TimeSpan HoverCheckInterval = TimeSpan.FromMilliseconds(400);

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hoverTimer;
    private InputNonClientPointerSource? _nonClient;
    private IntPtr _hwnd;
    private bool _controlsShown;
    private bool _spaceHandled;
    private DateTime _revealUntil;

    public MiniPlayerView()
    {
        InitializeComponent();
        _hoverTimer = DispatcherQueue.CreateTimer();
        _hoverTimer.Interval = HoverCheckInterval;
        _hoverTimer.Tick += (_, _) => CheckHover();

        // Most of the surface is the window's caption (drag) region, which gets no XAML pointer input: XAML events
        // cover the buttons, the non-client source covers the rest.
        Root.AddHandler(PointerMovedEvent, new PointerEventHandler((_, _) => ShowControls()), handledEventsToo: true);
        Root.PointerEntered += (_, _) => ShowControls();
        GotFocus += (_, _) =>
        {
            if (FocusManager.GetFocusedElement(XamlRoot) is Control { FocusState: FocusState.Keyboard })
            {
                ShowControls();
            }
        };

        foreach (var element in InteractiveElements)
        {
            element.SizeChanged += (_, _) => InteractiveLayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        Root.SizeChanged += (_, _) => InteractiveLayoutChanged?.Invoke(this, EventArgs.Empty);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>The buttons moved or resized (the window updates its pass-through regions).</summary>
    public event EventHandler? InteractiveLayoutChanged;

    public event EventHandler? ExpandRequested;

    public event EventHandler? CloseRequested;

    public PlayerViewModel ViewModel { get; } = App.GetService<PlayerViewModel>();

    /// <summary>The draggable surface, for <see cref="Window.SetTitleBar"/>.</summary>
    public UIElement DragRegionElement => DragRegion;

    /// <summary>Controls inside the drag surface that must stay clickable.</summary>
    public IReadOnlyList<FrameworkElement> InteractiveElements => [ExpandButton, CloseButton, PreviousButton, PlayPauseButton, NextButton];

    /// <summary>Called when the mini player appears: shows the controls for a moment and takes keyboard focus.</summary>
    public void Activate(TimeSpan reveal)
    {
        _revealUntil = DateTime.UtcNow + reveal;
        ShowControls();

        // The view itself (no focus visual), so Space works at once and Tab moves on to the buttons.
        Focus(FocusState.Programmatic);
    }

    /// <summary>Called when the mini player is hidden again.</summary>
    public void Deactivate()
    {
        _hoverTimer.Stop();
        _revealUntil = default;
        _controlsShown = false;
        VisualStateManager.GoToState(this, "ControlsHidden", false);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_nonClient is not null || XamlRoot?.ContentIslandEnvironment is not { } environment)
        {
            return;
        }

        _hwnd = Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
        _nonClient = InputNonClientPointerSource.GetForWindowId(environment.AppWindowId);
        _nonClient.PointerEntered += OnNonClientPointer;
        _nonClient.PointerMoved += OnNonClientPointer;

        // Shown for the first time: Activate ran before the view could take focus.
        if (Visibility == Visibility.Visible)
        {
            Focus(FocusState.Programmatic);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _hoverTimer.Stop();
        if (_nonClient is not null)
        {
            _nonClient.PointerEntered -= OnNonClientPointer;
            _nonClient.PointerMoved -= OnNonClientPointer;
            _nonClient = null;
        }
    }

    private void OnNonClientPointer(InputNonClientPointerSource sender, NonClientPointerEventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ShowControls();
        }
        else
        {
            DispatcherQueue.TryEnqueue(ShowControls);
        }
    }

    private void ShowControls()
    {
        if (Visibility != Visibility.Visible)
        {
            return;
        }

        if (!_controlsShown)
        {
            _controlsShown = true;
            VisualStateManager.GoToState(this, "ControlsShown", true);
        }

        _hoverTimer.Start();
    }

    // Leaving the window raises no reliable event for the caption region, so poll the cursor while the controls show.
    private void CheckHover()
    {
        if (DateTime.UtcNow < _revealUntil || IsPointerOverWindow() || HasKeyboardFocusInside())
        {
            return;
        }

        _hoverTimer.Stop();
        _controlsShown = false;
        VisualStateManager.GoToState(this, "ControlsHidden", true);
    }

    private bool IsPointerOverWindow() =>
        _hwnd != IntPtr.Zero
        && Win32.GetCursorPos(out var cursor)
        && Win32.GetWindowRect(_hwnd, out var rect)
        && cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;

    private bool HasKeyboardFocusInside() =>
        XamlRoot is not null && FocusManager.GetFocusedElement(XamlRoot) is Control { FocusState: FocusState.Keyboard } focused
        && InteractiveElements.Contains(focused);

    // Space = play/pause, Ctrl+Left/Right = previous/next (the shell's shortcuts, which are hidden with it).
    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var modifiers = ShortcutFocusPolicy.CurrentModifiers();
        if (e.Key == VirtualKey.Space && modifiers == VirtualKeyModifiers.None)
        {
            if (ShortcutFocusPolicy.Classify(XamlRoot) != ShortcutFocus.None)
            {
                return;
            }

            if (!e.KeyStatus.WasKeyDown)
            {
                ViewModel.PlayPauseCommand.Execute(null);
            }

            e.Handled = _spaceHandled = true;
            return;
        }

        if (modifiers == VirtualKeyModifiers.Control && e.Key is VirtualKey.Left or VirtualKey.Right)
        {
            if (!e.KeyStatus.WasKeyDown)
            {
                (e.Key == VirtualKey.Left ? ViewModel.PreviousCommand : ViewModel.NextCommand).Execute(null);
            }

            e.Handled = true;
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

    private void OnExpandClick(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke(this, EventArgs.Empty);

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
