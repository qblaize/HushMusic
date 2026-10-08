using Microsoft.UI.Xaml.Media.Imaging;
using HushMusic.Core.Models;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// Apple Music style header for album and playlist pages: a blurred wash of the artwork behind large art,
/// an uppercase eyebrow, the title, accent artist links, a clamped description and the page's action buttons.
/// Place it so it reaches the page edges (e.g. a negative margin equal to the list padding).
/// While <see cref="IsLoading"/> the text column shows placeholders (only the buttons when a preview title is known).
/// </summary>
public sealed partial class DetailHeader : UserControl
{
    private const double CompactBreakpoint = 680;
    private const double CompactArtSize = 168;

    public static readonly DependencyProperty ArtUrlProperty = DependencyProperty.Register(
        nameof(ArtUrl), typeof(string), typeof(DetailHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty PreviewArtUrlProperty = DependencyProperty.Register(
        nameof(PreviewArtUrl), typeof(string), typeof(DetailHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty PlaceholderGlyphProperty = DependencyProperty.Register(
        nameof(PlaceholderGlyph), typeof(string), typeof(DetailHeader), new PropertyMetadata(""));

    public static readonly DependencyProperty EyebrowProperty = DependencyProperty.Register(
        nameof(Eyebrow), typeof(string), typeof(DetailHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(DetailHeader), new PropertyMetadata(string.Empty, OnStateChanged));

    public static readonly DependencyProperty ArtistsProperty = DependencyProperty.Register(
        nameof(Artists), typeof(object), typeof(DetailHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(DetailHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions), typeof(object), typeof(DetailHeader), new PropertyMetadata(null));

    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading), typeof(bool), typeof(DetailHeader), new PropertyMetadata(false, OnStateChanged));

    private readonly double _artSize;

    public DetailHeader()
    {
        InitializeComponent();
        _artSize = Art.Width;
        UpdateState();
    }

    public string? ArtUrl
    {
        get => (string?)GetValue(ArtUrlProperty);
        set => SetValue(ArtUrlProperty, value);
    }

    /// <summary>A smaller art URL that is probably cached already (the card that was clicked); shown until <see cref="ArtUrl"/> loads.</summary>
    public string? PreviewArtUrl
    {
        get => (string?)GetValue(PreviewArtUrlProperty);
        set => SetValue(PreviewArtUrlProperty, value);
    }

    public string PlaceholderGlyph
    {
        get => (string)GetValue(PlaceholderGlyphProperty);
        set => SetValue(PlaceholderGlyphProperty, value);
    }

    /// <summary>Meta line above the title ("Album · 2013"); shown uppercase.</summary>
    public string? Eyebrow
    {
        get => (string?)GetValue(EyebrowProperty);
        set => SetValue(EyebrowProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Artists (album) or author (playlist); shown as accent links.</summary>
    public IReadOnlyList<ArtistRef>? Artists
    {
        get => GetValue(ArtistsProperty) as IReadOnlyList<ArtistRef>;
        set => SetValue(ArtistsProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>The button row (Play, Shuffle, …).</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    /// <summary>The page is still loading: placeholders instead of the missing text and the buttons.</summary>
    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    /// <summary>The cover element (connected animation source/target).</summary>
    internal UIElement ArtElement => Art;

    /// <summary>True once the cover shows real artwork (not the placeholder glyph).</summary>
    internal bool IsArtLoaded => IsLoadedImage(ArtImage) || IsLoadedImage(PreviewImage);

    /// <summary>The wash prefers the preview art: it is cached, and at 24 px both look the same.</summary>
    public string? WashUrl(string? artUrl, string? previewArtUrl) => previewArtUrl ?? artUrl;

    private static bool IsLoadedImage(Image image) => image.Source is BitmapImage { PixelWidth: > 0 };

    private static void OnStateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((DetailHeader)d).UpdateState();

    private void UpdateState()
    {
        var showText = !IsLoading || !string.IsNullOrEmpty(Title);
        TextSkeleton.Visibility = showText ? Visibility.Collapsed : Visibility.Visible;
        Reveal.SetIsShown(TextPanel, showText);
        ActionsSkeleton.Visibility = IsLoading ? Visibility.Visible : Visibility.Collapsed;
        Reveal.SetIsShown(ActionsPresenter, !IsLoading);
    }

    // Narrow windows get smaller art so the title column keeps a readable width.
    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var size = e.NewSize.Width < CompactBreakpoint ? CompactArtSize : _artSize;
        if (Art.Width != size)
        {
            Art.Width = size;
            Art.Height = size;
        }
    }
}
