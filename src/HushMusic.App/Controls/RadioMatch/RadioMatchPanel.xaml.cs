using System.ComponentModel;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using HushMusic.App.Helpers;
using HushMusic.App.ViewModels.RadioMatch;

namespace HushMusic.App.Controls.RadioMatch;

/// <summary>
/// Content of the "On YouTube Music" flyout. Logic lives in <see cref="RadioMatchViewModel"/>; this is UI glue: row hover,
/// and focus on the first result once the results arrive. Open it with <see cref="RadioMatchFlyout.ShowAt"/>.
/// </summary>
public sealed partial class RadioMatchPanel : UserControl
{
    public RadioMatchPanel(RadioMatchViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        Loaded += (_, _) => ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public RadioMatchViewModel ViewModel { get; }

    /// <summary>"ON YOUTUBE MUSIC" in the Hush design, sentence case in the Windows one.</summary>
    public string Eyebrow { get; } = Format.SectionHeader("On YouTube Music");

    public string SearchToolTip => $"Search for “{ViewModel.Query}”";

    // The flyout focused the first thing it could while searching (the footer link); the best match is a better start.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RadioMatchViewModel.State) || !ViewModel.IsFound)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            // The list was collapsed until now: lay it out so its rows exist.
            Results.UpdateLayout();
            if (XamlRoot is { } root && ReferenceEquals(FocusManager.GetFocusedElement(root), SearchButton)
                && Results.ContainerFromIndex(0) is DependencyObject container && VisualTreeHelper.GetChildrenCount(container) > 0
                && VisualTreeHelper.GetChild(container, 0) is FrameworkElement row
                && row.FindName("PlayArea") is Control first)
            {
                first.Focus(SearchButton.FocusState == FocusState.Keyboard ? FocusState.Keyboard : FocusState.Programmatic);
            }
        });
    }

    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e) => SetHover(sender, true);

    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e) => SetHover(sender, false);

    private static void SetHover(object row, bool hover)
    {
        if ((row as FrameworkElement)?.FindName("HoverFill") is UIElement fill)
        {
            fill.Opacity = hover ? 1 : 0;
        }
    }
}
