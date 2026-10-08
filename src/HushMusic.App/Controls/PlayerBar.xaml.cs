using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using HushMusic.App.Helpers;
using HushMusic.App.ViewModels.NowPlaying;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Controls;

/// <summary>
/// Floating glass player bar. Logic lives in <see cref="PlayerViewModel"/> and <see cref="NowPlayingViewModel"/>; this
/// tracks seek drags and accepts tracks dropped from pages (added to the queue).
/// </summary>
public sealed partial class PlayerBar : UserControl
{
    public PlayerBar()
    {
        InitializeComponent();

        // Slider marks pointer events handled, so listen with handledEventsToo to know when a drag starts and ends.
        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSeekPointerPressed), handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSeekPointerReleased), handledEventsToo: true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSeekPointerReleased), handledEventsToo: true);
    }

    public PlayerViewModel ViewModel { get; } = App.GetService<PlayerViewModel>();

    public NowPlayingViewModel NowPlaying { get; } = App.GetService<NowPlayingViewModel>();

    private void OnSeekPointerPressed(object sender, PointerRoutedEventArgs e) => ViewModel.BeginSeek();

    private void OnSeekPointerReleased(object sender, PointerRoutedEventArgs e) => ViewModel.EndSeek();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!TrackDragData.Has(e.DataView))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to queue";
        e.DragUIOverride.IsGlyphVisible = false;
        DropHighlight.Visibility = Visibility.Visible;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropHighlight.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        if (!TrackDragData.Has(e.DataView))
        {
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            if (await TrackDragData.TryGetAsync(e.DataView) is { Count: > 0 } tracks)
            {
                NowPlaying.Queue.AddToQueue(tracks);
            }
        }
        catch (Exception)
        {
            // The package expired or came from elsewhere: nothing to add.
        }
        finally
        {
            deferral.Complete();
        }
    }
}
