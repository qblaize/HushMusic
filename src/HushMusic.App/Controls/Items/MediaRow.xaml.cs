using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using HushMusic.Core.Models;

namespace HushMusic.App.Controls.Items;

/// <summary>Compact list row for albums, artists and playlists (search results, history, pickers).</summary>
public sealed partial class MediaRow : UserControl
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(object), typeof(MediaRow), new PropertyMetadata(null, OnItemChanged));

    private readonly CornerRadius _defaultCorner;

    public MediaRow()
    {
        InitializeComponent();
        _defaultCorner = ArtHost.CornerRadius;
    }

    public MediaItem? Item
    {
        get => GetValue(ItemProperty) as MediaItem;
        set => SetValue(ItemProperty, value);
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (MediaRow)d;
        var item = e.NewValue as MediaItem;
        row.ArtHost.CornerRadius = item is Artist ? new CornerRadius(row.ArtHost.Width / 2) : row._defaultCorner;
        row.SetHover(false);
        AutomationProperties.SetName(row, item is null ? string.Empty : $"{ItemFormat.Kind(item)}: {item.Title}");
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is { RasterizationScale: > 0 } root)
        {
            Hairline.Height = 1 / root.RasterizationScale;
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e) => SetHover(true);

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => SetHover(false);

    private void SetHover(bool hover)
    {
        HoverFill.Opacity = hover ? 1 : 0;
        Hairline.Opacity = hover ? 0 : 1;
        Chevron.Opacity = hover ? 1 : 0;
    }
}
