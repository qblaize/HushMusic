using System.Globalization;
using HushMusic.App.Helpers;
using HushMusic.Core.Models;

namespace HushMusic.App.Controls.Items;

/// <summary>Display text and art selection for media items, used by x:Bind function bindings and view models.</summary>
public static class ItemFormat
{
    private const string Separator = " · ";

    // InnerTube thumbnails come in a few fixed sizes (60, 120, 226, 544...); ask for ~1.5x the display size.
    public static string? CardArt(MediaItem? item) => item?.ThumbnailFor(226)?.Url;

    public static string? RowArt(MediaItem? item) => item?.ThumbnailFor(96)?.Url;

    public static string? HeaderArt(MediaItem? item) => item?.ThumbnailFor(400)?.Url;

    public static string? HeroArt(MediaItem? item) => item?.BestThumbnail?.Url;

    public static string Title(MediaItem? item) => item?.Title ?? string.Empty;

    /// <summary>Second line under a card.</summary>
    public static string CardSubtitle(MediaItem? item) => item switch
    {
        Track track => Join(track.ArtistsText, track.Views),
        Album album => Join(AlbumTypeText(album.Type), album.ArtistsText, album.Year),
        Playlist playlist => Join(playlist.Author?.Name, TrackCount(playlist.TrackCount)),
        Artist artist => SubscribersText(artist.Subscribers) ?? "Artist",
        _ => string.Empty,
    };

    /// <summary>Second line of a list row; starts with the item kind because lists mix kinds.</summary>
    public static string RowSubtitle(MediaItem? item) => item switch
    {
        Track track => Join(Kind(track), track.ArtistsText, track.Album?.Name, Format.Duration(track.Duration)),
        Album album => Join(AlbumTypeText(album.Type) ?? "Album", album.ArtistsText, album.Year),
        Playlist playlist => Join("Playlist", playlist.Author?.Name, TrackCount(playlist.TrackCount)),
        Artist artist => Join("Artist", SubscribersText(artist.Subscribers)),
        _ => string.Empty,
    };

    /// <summary>
    /// Artists line of a track row; non-songs are prefixed with their kind, except on album rows
    /// (<paramref name="isAlbumRow"/>) where signed-out albums list official videos and the label is noise.
    /// </summary>
    public static string TrackSubtitle(Track? track, bool isAlbumRow) =>
        track is null ? string.Empty : Join(KindPrefix(track, isAlbumRow), track.ArtistsText);

    /// <summary>"Video" / "Episode" in front of a row's artists; null for songs and on album rows.</summary>
    public static string? KindPrefix(Track? track, bool isAlbumRow) =>
        track is null || track.Type == TrackType.Song || isAlbumRow ? null : Kind(track);

    public static IReadOnlyList<ArtistRef> Artists(Track? track) => track?.Artists ?? [];

    public static string AlbumName(Track? track) => track?.Album?.Name ?? string.Empty;

    public static string Duration(Track? track) => Format.Duration(track?.Duration);

    public static double AvailabilityOpacity(Track? track) => track is { IsAvailable: false } ? 0.45 : 1.0;

    public static Visibility ExplicitVisibility(MediaItem? item) =>
        item is Track { IsExplicit: true } or Album { IsExplicit: true } ? Visibility.Visible : Visibility.Collapsed;

    public static string PlaceholderGlyph(MediaItem? item) => item switch
    {
        Artist => "",
        Album => "",
        Playlist => "",
        _ => "",
    };

    /// <summary>Round art for artists (XAML clamps an oversized radius to a circle), rounded square otherwise.</summary>
    public static CornerRadius ArtCorner(MediaItem? item) => item is Artist ? new CornerRadius(999) : new CornerRadius(10);

    public static string Number(int number) => number > 0 ? number.ToString(CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>Eyebrow text ("ALBUM · 2013"): TextBlock has no text-transform, so the casing is done here.</summary>
    public static string Upper(string? text) => text?.ToUpper(CultureInfo.CurrentCulture) ?? string.Empty;

    /// <summary>"TOP RESULT · ARTIST".</summary>
    public static string TopResultEyebrow(MediaItem? item) => Upper(Join("Top result", Kind(item)));

    public static string Kind(MediaItem? item) => item switch
    {
        Track { Type: TrackType.Video } => "Video",
        Track { Type: TrackType.Episode } => "Episode",
        Track => "Song",
        Album album => AlbumTypeText(album.Type) ?? "Album",
        Artist => "Artist",
        Playlist => "Playlist",
        _ => string.Empty,
    };

    public static string? AlbumTypeText(AlbumType type) => type switch
    {
        AlbumType.Album => "Album",
        AlbumType.Single => "Single",
        AlbumType.EP => "EP",
        _ => null,
    };

    public static string? TrackCount(int? count) => count switch
    {
        null => null,
        1 => "1 song",
        _ => string.Format(CultureInfo.CurrentCulture, "{0:N0} songs", count),
    };

    /// <summary>InnerTube sometimes gives "1.2M" and sometimes "1.2M subscribers".</summary>
    public static string? SubscribersText(string? subscribers) =>
        string.IsNullOrWhiteSpace(subscribers) ? null
        : subscribers.Contains("subscriber", StringComparison.OrdinalIgnoreCase) ? subscribers
        : $"{subscribers} subscribers";

    public static string? PrivacyText(PrivacyStatus? privacy) => privacy?.ToString();

    public static string Join(params string?[] parts) =>
        string.Join(Separator, parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
}
