using HushMusic.Core.Models;

namespace HushMusic.App.Views.Pages;

/// <summary>A "Moods &amp; genres" category tile, tinted with the colour YouTube Music gives the category.</summary>
public sealed partial class ExploreMoodTile : UserControl
{
    public static readonly DependencyProperty CategoryProperty = DependencyProperty.Register(
        nameof(Category), typeof(MoodCategory), typeof(ExploreMoodTile), new PropertyMetadata(null));

    public ExploreMoodTile()
    {
        InitializeComponent();
    }

    /// <summary>Raised when the tile is invoked; the sender is the tile, see <see cref="Category"/>.</summary>
    public event RoutedEventHandler? Click;

    public MoodCategory? Category
    {
        get => (MoodCategory?)GetValue(CategoryProperty);
        set => SetValue(CategoryProperty, value);
    }

    private void OnClick(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
}
