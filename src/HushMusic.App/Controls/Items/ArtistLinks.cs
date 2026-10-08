using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Models;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// Comma-separated artist names; artists with a channel id are links to their page.
/// <see cref="TextStyle"/> styles the text (wrapping, trimming, size); <see cref="LinkForeground"/> colours the links.
/// </summary>
public sealed partial class ArtistLinks : UserControl
{
    public static readonly DependencyProperty ArtistsProperty = DependencyProperty.Register(
        nameof(Artists), typeof(object), typeof(ArtistLinks), new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty PrefixProperty = DependencyProperty.Register(
        nameof(Prefix), typeof(string), typeof(ArtistLinks), new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty LinkForegroundProperty = DependencyProperty.Register(
        nameof(LinkForeground), typeof(Brush), typeof(ArtistLinks), new PropertyMetadata(null, OnContentChanged));

    public static readonly DependencyProperty TextStyleProperty = DependencyProperty.Register(
        nameof(TextStyle), typeof(Style), typeof(ArtistLinks), new PropertyMetadata(null, OnTextStyleChanged));

    public static readonly DependencyProperty IsLinkTabStopProperty = DependencyProperty.Register(
        nameof(IsLinkTabStop), typeof(bool), typeof(ArtistLinks), new PropertyMetadata(true, OnContentChanged));

    private readonly TextBlock _text = new() { TextWrapping = TextWrapping.Wrap };

    public ArtistLinks()
    {
        IsTabStop = false;
        Content = _text;
        Visibility = Visibility.Collapsed;
    }

    public IReadOnlyList<ArtistRef>? Artists
    {
        get => GetValue(ArtistsProperty) as IReadOnlyList<ArtistRef>;
        set => SetValue(ArtistsProperty, value);
    }

    /// <summary>Plain text before the names, e.g. "Video" (joined with " · ").</summary>
    public string? Prefix
    {
        get => (string?)GetValue(PrefixProperty);
        set => SetValue(PrefixProperty, value);
    }

    /// <summary>Link colour; null keeps the text colour.</summary>
    public Brush? LinkForeground
    {
        get => (Brush?)GetValue(LinkForegroundProperty);
        set => SetValue(LinkForegroundProperty, value);
    }

    public Style? TextStyle
    {
        get => (Style?)GetValue(TextStyleProperty);
        set => SetValue(TextStyleProperty, value);
    }

    /// <summary>False in list rows, where Tab should move between rows rather than into every artist link.</summary>
    public bool IsLinkTabStop
    {
        get => (bool)GetValue(IsLinkTabStopProperty);
        set => SetValue(IsLinkTabStopProperty, value);
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ArtistLinks)d).Rebuild();

    private static void OnTextStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var links = (ArtistLinks)d;
        links._text.Style = e.NewValue as Style;
        links.Rebuild();
    }

    private void Rebuild()
    {
        _text.Inlines.Clear();

        // Separators and the prefix take the link colour too, so a line of links reads as one colour.
        if (LinkForeground is { } linkBrush)
        {
            _text.Foreground = linkBrush;
        }
        else
        {
            _text.ClearValue(TextBlock.ForegroundProperty);
        }

        var artists = Artists ?? [];
        if (!string.IsNullOrWhiteSpace(Prefix))
        {
            _text.Inlines.Add(new Run { Text = artists.Count > 0 ? $"{Prefix} · " : Prefix });
        }

        for (var i = 0; i < artists.Count; i++)
        {
            if (i > 0)
            {
                _text.Inlines.Add(new Run { Text = ", " });
            }

            var artist = artists[i];
            if (string.IsNullOrWhiteSpace(artist.BrowseId))
            {
                _text.Inlines.Add(new Run { Text = artist.Name });
                continue;
            }

            var channelId = artist.BrowseId;
            var link = new Hyperlink
            {
                UnderlineStyle = UnderlineStyle.None,
                IsTabStop = IsLinkTabStop,
                Foreground = _text.Foreground,
            };
            link.Inlines.Add(new Run { Text = artist.Name });
            link.Click += (_, _) => App.GetService<IMediaItemActions>().OpenArtist(channelId);
            _text.Inlines.Add(link);
        }

        Visibility = artists.Count > 0 || !string.IsNullOrWhiteSpace(Prefix) ? Visibility.Visible : Visibility.Collapsed;
    }
}
