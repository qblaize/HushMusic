using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using HushMusic.App.Controls.Radio;
using HushMusic.App.Helpers;
using HushMusic.App.Services.Shell;

namespace HushMusic.App.Controls.Shell;

/// <summary>
/// The small cover of a sidebar pin: album or playlist art (rounded square), an artist photo (round), or a station's
/// logo. Art comes through the image disk cache; a glyph shows until it has loaded. Size, corners and background come
/// from <c>RailPinArtStyle</c>.
/// </summary>
public sealed partial class RailPinArt : Grid
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
        nameof(Url), typeof(string), typeof(RailPinArt), new PropertyMetadata(null, (d, _) => ((RailPinArt)d).UpdateArt()));

    public static readonly DependencyProperty StationNameProperty = DependencyProperty.Register(
        nameof(StationName), typeof(string), typeof(RailPinArt), new PropertyMetadata(null, (d, _) => ((RailPinArt)d).UpdateArt()));

    public static readonly DependencyProperty StationLogoUrlProperty = DependencyProperty.Register(
        nameof(StationLogoUrl), typeof(string), typeof(RailPinArt), new PropertyMetadata(null, (d, _) => ((RailPinArt)d).UpdateArt()));

    public static readonly DependencyProperty IsRoundProperty = DependencyProperty.Register(
        nameof(IsRound), typeof(bool), typeof(RailPinArt), new PropertyMetadata(false, (d, _) => ((RailPinArt)d).UpdateShape()));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(RailPinArt), new PropertyMetadata(string.Empty, (d, e) => ((RailPinArt)d)._placeholder.Glyph = e.NewValue as string ?? string.Empty));

    private readonly FontIcon _placeholder = new() { IsHitTestVisible = false };
    // Centred, so art that isn't square is cropped to its middle.
    private readonly Image _image = new() { Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private StationArt? _station;

    public RailPinArt()
    {
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        _placeholder.Foreground = ThemeResources.TrackingBrush("TextTertiaryColor");
        Children.Add(_placeholder);
        Children.Add(_image);
        SizeChanged += (_, e) =>
        {
            _placeholder.FontSize = Math.Max(10, Math.Round(e.NewSize.Width * 0.45));
            UpdateShape();
        };
    }

    /// <summary>Album, playlist or artist art.</summary>
    public string? Url
    {
        get => (string?)GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    /// <summary>Set (not empty) for a station pin: its logo, or generated art with these initials, shows instead of <see cref="Url"/>.</summary>
    public string? StationName
    {
        get => (string?)GetValue(StationNameProperty);
        set => SetValue(StationNameProperty, value);
    }

    public string? StationLogoUrl
    {
        get => (string?)GetValue(StationLogoUrlProperty);
        set => SetValue(StationLogoUrlProperty, value);
    }

    /// <summary>Artists are round.</summary>
    public bool IsRound
    {
        get => (bool)GetValue(IsRoundProperty);
        set => SetValue(IsRoundProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    private void UpdateArt()
    {
        // x:Bind hands a null string over as empty.
        if (StationName is { Length: > 0 } name)
        {
            ImageLoader.SetUrl(_image, null);
            if (_station is null)
            {
                _station = new StationArt();
                Children.Add(_station);
            }

            _station.StationName = name;
            _station.LogoUrl = StationLogoUrl;
            _placeholder.Visibility = Visibility.Collapsed;
            return;
        }

        if (_station is not null)
        {
            Children.Remove(_station);
            _station = null;
        }

        _placeholder.Visibility = Visibility.Visible;
        ImageLoader.SetUrl(_image, Url);
    }

    // Round: half the laid-out size. Otherwise the style's corners.
    private void UpdateShape()
    {
        var size = double.IsNaN(Width) ? ActualWidth : Width;
        if (IsRound && size > 0)
        {
            CornerRadius = new CornerRadius(size / 2);
        }
        else if (!IsRound)
        {
            ClearValue(CornerRadiusProperty);
        }
    }
}
