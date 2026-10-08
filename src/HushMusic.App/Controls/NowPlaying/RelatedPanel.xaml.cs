using System.ComponentModel;
using HushMusic.App.ViewModels.NowPlaying;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>The "Related" tab. Logic lives in <see cref="RelatedViewModel"/>; new shelves start at the top.</summary>
public sealed partial class RelatedPanel : UserControl
{
    public RelatedPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public RelatedViewModel ViewModel { get; } = App.GetService<RelatedViewModel>();

    /// <summary>Round art for artists (an oversized radius clamps to a circle), rounded squares otherwise.</summary>
    public static CornerRadius ArtCorner(bool isRound) => isRound ? new CornerRadius(999) : new CornerRadius(10);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RelatedViewModel.Shelves))
        {
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        }
    }
}
