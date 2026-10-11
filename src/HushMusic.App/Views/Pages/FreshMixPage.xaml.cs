using Microsoft.UI.Xaml.Navigation;
using HushMusic.App.Controls.Items;
using HushMusic.App.ViewModels.Pages;

namespace HushMusic.App.Views.Pages;

public sealed partial class FreshMixPage : Page
{
    // Narrow windows get a smaller cover, like the album and playlist headers.
    private const double CompactBreakpoint = 680;
    private const double CompactArtSize = 168;

    private readonly double _artSize;

    public FreshMixPage()
    {
        InitializeComponent();
        _artSize = Art.Width;
    }

    public FreshMixViewModel ViewModel { get; } = App.GetService<FreshMixViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.OnNavigatedToAsync(e.Parameter);

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    private void OnTrackClick(object sender, ItemClickEventArgs e)
    {
        if (!TrackSelectionList.HandleClick(sender, e.ClickedItem))
        {
            ViewModel.PlayTrackCommand.Execute(e.ClickedItem);
        }
    }

    private void OnHeaderSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var size = e.NewSize.Width < CompactBreakpoint ? CompactArtSize : _artSize;
        if (Art.Width != size)
        {
            Art.Width = size;
            Art.Height = size;
        }
    }
}
