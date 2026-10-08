using Microsoft.UI.Xaml.Input;
using HushMusic.App.ViewModels.Pages;

namespace HushMusic.App.Controls.Radio;

/// <summary>
/// A radio station card: logo (or generated art), name, genres, country and quality. Clicking plays it; hovering
/// reveals play and favourite buttons; right-click has Play, favourite and Open website. Logic lives in <see cref="StationItem"/>.
/// </summary>
public sealed partial class StationCard : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(StationItem), typeof(StationCard), new PropertyMetadata(null, OnItemChanged));

    private bool _isHovered;

    public StationCard()
    {
        InitializeComponent();
        FavoriteButton.GotFocus += (_, _) => SetHover(true);
        FavoriteButton.LostFocus += (_, _) => SetHover(false);
        CardButton.GotFocus += (_, _) =>
        {
            if (CardButton.FocusState == FocusState.Keyboard)
            {
                SetHover(true);
            }
        };
        CardButton.LostFocus += (_, _) => SetHover(false);
    }

    public StationItem? Item
    {
        get => (StationItem?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Recycled for another station while lifted: drop the hover look without animating.
        var card = (StationCard)d;
        if (card._isHovered)
        {
            card._isHovered = false;
            card.Apply(useTransitions: false);
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => SetHover(true);

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => SetHover(false);

    private void SetHover(bool hover)
    {
        if (hover == _isHovered)
        {
            return;
        }

        _isHovered = hover;
        Apply(useTransitions: true);
    }

    private void Apply(bool useTransitions)
    {
        VisualStateManager.GoToState(this, _isHovered ? "Hover" : "Rest", useTransitions);
        FavoriteButton.IsHitTestVisible = _isHovered;
        PlayButton.IsHitTestVisible = _isHovered;
    }
}
