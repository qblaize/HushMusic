using Microsoft.UI.Xaml.Controls.Primitives;
using HushMusic.App.Controls.RadioMatch;
using HushMusic.App.ViewModels.NowPlaying;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// The "Heard on air" tab for live radio, with the station's "Save songs to a playlist" switch. Logic lives in
/// <see cref="RadioHeardViewModel"/>; a click on a song opens "On YouTube Music" beside it.
/// </summary>
public sealed partial class HeardOnAirPanel : UserControl
{
    public HeardOnAirPanel()
    {
        InitializeComponent();

        // The list's margin in the XAML clears the fades.
        EdgeFade.Attach(HeardHost, HeardScroller, top: 24, bottom: 48);
    }

    public RadioHeardViewModel ViewModel { get; } = App.GetService<RadioHeardViewModel>();

    private void OnSongClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: HeardSongViewModel song } row)
        {
            RadioMatchFlyout.ShowAt(row, song.Song, ViewModel.StationName, FlyoutPlacementMode.LeftEdgeAlignedTop);
        }
    }
}
