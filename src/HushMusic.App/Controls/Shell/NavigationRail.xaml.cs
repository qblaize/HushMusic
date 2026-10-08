using Microsoft.UI.Dispatching;
using HushMusic.App.Helpers;

namespace HushMusic.App.Controls.Shell;

/// <summary>The left icon rail: navigation items, search, account and settings. Selection follows <see cref="ShellViewModel.CurrentPage"/>.</summary>
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
            item.LabelRequested += (sender, show) => LabelRequested?.Invoke(sender, show);
        }
    }

    /// <summary>A rail item wants its floating label shown (true) or hidden (false). Sender: the <see cref="RailButton"/>.</summary>
    public event EventHandler<bool>? LabelRequested;

    public ShellViewModel ViewModel { get; } = App.GetService<ShellViewModel>();

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
