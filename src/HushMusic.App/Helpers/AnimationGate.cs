using Microsoft.UI.Xaml.Media;

namespace HushMusic.App.Helpers;

/// <summary>
/// Lets an endless animation (equaliser bars, the LIVE pulse, skeleton breathing, the Now Playing drift) run only while
/// it can be seen: its element is loaded, it and every ancestor are visible, and the main window is on screen (not
/// minimized or in the notification area). A running animation keeps the compositor drawing frames, also inside a
/// collapsed view or a hidden window, so one left running there costs CPU for nothing.
/// </summary>
/// <remarks>
/// The owner says when it wants to animate (<see cref="IsWanted"/>), and the callback it passed in is called when the
/// animation may start (true) or must stop (false). While it is wanted, the gate re-checks after every layout pass (showing or
/// collapsing any ancestor causes one) and whenever the window is shown or hidden.
/// </remarks>
internal sealed class AnimationGate
{
    private readonly FrameworkElement _element;
    private readonly Action<bool> _changed;
    private bool _wanted;
    private bool _listening;

    public AnimationGate(FrameworkElement element, Action<bool> changed)
    {
        _element = element;
        _changed = changed;
        element.Loaded += (_, _) => Update();
        element.Unloaded += (_, _) => Update();
    }

    /// <summary>The animation may run now.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The owner wants its animation (e.g. the track is playing and the element is meant to show).</summary>
    public bool IsWanted
    {
        get => _wanted;
        set
        {
            if (_wanted != value)
            {
                _wanted = value;
                Update();
            }
        }
    }

    /// <summary>The element and all its ancestors are visible (it may still be scrolled out of view).</summary>
    public static bool IsInVisibleTree(UIElement element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Re-checks now (e.g. right after the owner changed its own visibility).</summary>
    public void Update()
    {
        var listen = _wanted && _element.IsLoaded;
        Listen(listen);
        var open = listen && !WindowPresence.Hides(_element) && IsInVisibleTree(_element);
        if (open != IsOpen)
        {
            IsOpen = open;
            _changed(open);
        }
    }

    private void Listen(bool listen)
    {
        if (listen == _listening)
        {
            return;
        }

        _listening = listen;
        if (listen)
        {
            _element.LayoutUpdated += OnLayoutUpdated;
            WindowPresence.Changed += OnWindowVisibilityChanged;
        }
        else
        {
            _element.LayoutUpdated -= OnLayoutUpdated;
            WindowPresence.Changed -= OnWindowVisibilityChanged;
        }
    }

    private void OnLayoutUpdated(object? sender, object e) => Update();

    private void OnWindowVisibilityChanged(object? sender, EventArgs e) => Update();
}
