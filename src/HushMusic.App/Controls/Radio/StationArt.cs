using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using HushMusic.App.Services.Radio;

namespace HushMusic.App.Controls.Radio;

/// <summary>
/// Square artwork for a radio station, from logos of any size and shape (most are favicons):
/// <list type="bullet">
/// <item>a big, square, opaque logo fills the square, like album art;</item>
/// <item>a small or non-square opaque logo sits centred on a blurred wash of its own colours;</item>
/// <item>a transparent logo sits on a light or dark plate, whichever it was drawn for;</item>
/// <item>no logo (or one that fails) shows a generated gradient with the station's initials.</item>
/// </list>
/// Logos are never upscaled more than about twice, so small ones stay crisp.
/// </summary>
public sealed partial class StationArt : Grid
{
    public static readonly DependencyProperty LogoUrlProperty = DependencyProperty.Register(
        nameof(LogoUrl), typeof(string), typeof(StationArt), new PropertyMetadata(null, (d, _) => ((StationArt)d).Reload()));

    public static readonly DependencyProperty StationNameProperty = DependencyProperty.Register(
        nameof(StationName), typeof(string), typeof(StationArt), new PropertyMetadata(null, (d, _) => ((StationArt)d).UpdateFallback()));

    // Plates are part of the artwork, not the UI theme: a transparent logo was drawn for one of these.
    private static readonly SolidColorBrush LightPlate = new(Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF7));
    private static readonly SolidColorBrush DarkPlate = new(Color.FromArgb(0xFF, 0x1C, 0x1C, 0x1E));

    private readonly LinearGradientBrush _gradient = new() { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(1, 1) };
    private readonly Border _fallback = new();
    private readonly TextBlock _monogram = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        TextLineBounds = TextLineBounds.Tight,
    };

    private readonly Image _wash = new() { Stretch = Stretch.UniformToFill, Visibility = Visibility.Collapsed };
    private readonly Border _washScrim = new() { Visibility = Visibility.Collapsed };
    private readonly Border _plate = new() { Visibility = Visibility.Collapsed };
    private readonly Image _logo = new();
    private readonly Grid _logoFrame = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private StationLogoInfo? _info;
    private double _laidOutSize;
    private int _version;

    public StationArt()
    {
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        _fallback.Background = _gradient;
        _fallback.Child = _monogram;
        _washScrim.Background = Application.Current.Resources["ArtHoverScrimBrush"] as Brush;
        _monogram.Foreground = Application.Current.Resources["OnArtForegroundBrush"] as Brush;
        _monogram.FontFamily = Application.Current.Resources["AppFontFamily"] as FontFamily;
        Children.Add(_fallback);
        Children.Add(_wash);
        Children.Add(_washScrim);
        Children.Add(_plate);
        _logoFrame.Children.Add(_logo);
        Children.Add(_logoFrame);
        _logo.ImageFailed += (_, _) => ShowFallback();
        SizeChanged += (_, e) =>
        {
            _monogram.FontSize = Math.Max(10, Math.Round(e.NewSize.Width * 0.3));
            if (_info is not null && Math.Abs(e.NewSize.Width - _laidOutSize) > 1)
            {
                Apply(_info);
            }
        };
    }

    public string? LogoUrl
    {
        get => (string?)GetValue(LogoUrlProperty);
        set => SetValue(LogoUrlProperty, value);
    }

    /// <summary>Used for the fallback's initials and colours.</summary>
    public string? StationName
    {
        get => (string?)GetValue(StationNameProperty);
        set => SetValue(StationNameProperty, value);
    }

    private static IStationLogos? Logos => App.Services?.GetService<IStationLogos>();

    /// <summary>"SomaFM Groove Salad" → "SG"; words like "radio" and "the" are skipped.</summary>
    internal static string Initials(string? name)
    {
        var words = (name ?? string.Empty)
            .Split([' ', '-', '_', '.', '|', '#', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetterOrDigit(w[0]) && w.ToLowerInvariant() is not ("radio" or "the" or "fm" or "1" or "and" or "&"))
            .ToList();
        return words.Count switch
        {
            0 => "♪",
            1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
            _ => string.Concat(char.ToUpperInvariant(words[0][0]), char.ToUpperInvariant(words[1][0])),
        };
    }

    /// <summary>Two related colours from the name, so a station always gets the same gradient.</summary>
    internal static (Color From, Color To) GradientFor(string? name)
    {
        var hash = 17;
        foreach (var c in name ?? string.Empty)
        {
            hash = unchecked((hash * 31) + char.ToLowerInvariant(c));
        }

        var hue = (hash & 0x7FFFFFFF) % 360;
        return (FromHsl(hue, 0.55, 0.46), FromHsl((hue + 38) % 360, 0.62, 0.30));
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        var chroma = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        var h = hue / 60;
        var x = chroma * (1 - Math.Abs((h % 2) - 1));
        var (r, g, b) = h switch
        {
            < 1 => (chroma, x, 0.0),
            < 2 => (x, chroma, 0.0),
            < 3 => (0.0, chroma, x),
            < 4 => (0.0, x, chroma),
            < 5 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        var m = lightness - (chroma / 2);
        return Color.FromArgb(0xFF, (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private void UpdateFallback()
    {
        var (from, to) = GradientFor(StationName);
        _gradient.GradientStops.Clear();
        _gradient.GradientStops.Add(new GradientStop { Color = from, Offset = 0 });
        _gradient.GradientStops.Add(new GradientStop { Color = to, Offset = 1 });
        _monogram.Text = Initials(StationName);
    }

    private async void Reload()
    {
        var version = ++_version;
        _info = null;
        ShowFallback();
        var url = LogoUrl;
        if (string.IsNullOrWhiteSpace(url) || Logos is not { } logos)
        {
            return;
        }

        StationLogoInfo? info;
        try
        {
            info = await logos.GetAsync(url);
        }
        catch (Exception)
        {
            info = null;
        }

        if (version != _version || info is null)
        {
            return;
        }

        _info = info;
        Apply(info);
    }

    private void Apply(StationLogoInfo info)
    {
        var size = ActualWidth > 0 ? ActualWidth : Width;
        if (double.IsNaN(size) || size <= 0)
        {
            return; // laid out later (SizeChanged)
        }

        _laidOutSize = size;
        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        var physical = size * scale;
        var shortSide = Math.Min(info.Width, info.Height);
        var source = new Uri(info.LocalPath);

        if (info.IsOpaque && info.IsSquare && shortSide >= physical * 0.75)
        {
            // Big enough to fill the square, like album art.
            _logo.Stretch = Stretch.UniformToFill;
            _logoFrame.Width = double.NaN;
            _logoFrame.Height = double.NaN;
            _logoFrame.HorizontalAlignment = HorizontalAlignment.Stretch;
            _logoFrame.VerticalAlignment = VerticalAlignment.Stretch;
            _logoFrame.CornerRadius = default;
            _logo.Source = Bitmap(source, (int)Math.Ceiling(physical), info);
            Show(wash: false, plate: null);
            return;
        }

        // Centred, at most about twice its natural size, and between 35 % and 62 % (70 % on a plate) of the square.
        var max = size * (info.IsOpaque ? 0.62 : 0.7);
        var box = Math.Clamp(Math.Max(info.Width, info.Height) * 2 / scale, size * 0.35, max);
        _logo.Stretch = Stretch.Uniform;
        _logoFrame.Width = box;
        _logoFrame.Height = box;
        _logoFrame.HorizontalAlignment = HorizontalAlignment.Center;
        _logoFrame.VerticalAlignment = VerticalAlignment.Center;
        _logoFrame.CornerRadius = new CornerRadius(Math.Round(box * 0.12));
        _logo.Source = Bitmap(source, (int)Math.Ceiling(box * scale), info);
        if (info.IsOpaque)
        {
            _wash.Source = new BitmapImage { DecodePixelType = DecodePixelType.Physical, DecodePixelWidth = 8, UriSource = source };
            Show(wash: true, plate: null);
        }
        else
        {
            Show(wash: false, plate: info.IsLight ? DarkPlate : LightPlate);
        }
    }

    private static BitmapImage Bitmap(Uri source, int pixels, StationLogoInfo info)
    {
        var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Physical };

        // Decode big logos down to what is shown; small ones at their own size (upscaled by the GPU).
        if (Math.Max(info.Width, info.Height) > pixels)
        {
            if (info.Width >= info.Height)
            {
                bitmap.DecodePixelWidth = pixels;
            }
            else
            {
                bitmap.DecodePixelHeight = pixels;
            }
        }

        bitmap.UriSource = source;
        return bitmap;
    }

    private void Show(bool wash, Brush? plate)
    {
        _fallback.Visibility = Visibility.Collapsed;
        _wash.Visibility = wash ? Visibility.Visible : Visibility.Collapsed;
        _washScrim.Visibility = _wash.Visibility;
        _plate.Background = plate;
        _plate.Visibility = plate is null ? Visibility.Collapsed : Visibility.Visible;
        _logoFrame.Visibility = Visibility.Visible;
    }

    private void ShowFallback()
    {
        _fallback.Visibility = Visibility.Visible;
        _wash.Visibility = Visibility.Collapsed;
        _washScrim.Visibility = Visibility.Collapsed;
        _plate.Visibility = Visibility.Collapsed;
        _logoFrame.Visibility = Visibility.Collapsed;
        _logo.Source = null;
        _wash.Source = null;
    }
}
