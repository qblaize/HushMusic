using HushMusic.App.Helpers;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// The cover of a mix: four album covers in a square (one fills it when there are fewer than four). Set the size with
/// Width and Height, the rounding with CornerRadius.
/// </summary>
public sealed partial class CoverMosaic : UserControl
{
    public static readonly DependencyProperty CoversProperty = DependencyProperty.Register(
        nameof(Covers), typeof(object), typeof(CoverMosaic), new PropertyMetadata(null, OnCoversChanged));

    public static readonly DependencyProperty PlaceholderSizeProperty = DependencyProperty.Register(
        nameof(PlaceholderSize), typeof(double), typeof(CoverMosaic), new PropertyMetadata(32.0));

    private readonly Image[] _images;

    public CoverMosaic()
    {
        InitializeComponent();
        _images = [Cover0, Cover1, Cover2, Cover3];
    }

    /// <summary>The cover URLs (an <c>IReadOnlyList&lt;string&gt;</c>), in order.</summary>
    public object? Covers
    {
        get => GetValue(CoversProperty);
        set => SetValue(CoversProperty, value);
    }

    /// <summary>Size of the note shown until the covers load.</summary>
    public double PlaceholderSize
    {
        get => (double)GetValue(PlaceholderSizeProperty);
        set => SetValue(PlaceholderSizeProperty, value);
    }

    private static void OnCoversChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((CoverMosaic)d).Update();

    private void Update()
    {
        var urls = Covers as IReadOnlyList<string> ?? [];
        var quad = urls.Count >= 4;

        // One cover alone spans the whole square.
        Grid.SetRowSpan(Cover0, quad ? 1 : 2);
        Grid.SetColumnSpan(Cover0, quad ? 1 : 2);
        for (var i = 0; i < _images.Length; i++)
        {
            var url = i < urls.Count && (quad || i == 0) ? urls[i] : null;
            _images[i].Visibility = url is null ? Visibility.Collapsed : Visibility.Visible;
            ImageLoader.SetUrl(_images[i], url);
        }
    }
}
