using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features.Discord;

/// <summary>
/// Builds the SET_ACTIVITY "activity" object: a Listening activity whose status line reads "Listening to {artist}".
/// </summary>
public static partial class DiscordActivity
{
    /// <summary>Activity type 2: "Listening to".</summary>
    public const int ListeningType = 2;

    /// <summary>status_display_type 1: the member list shows the <c>state</c> field (the artist) after "Listening to".</summary>
    public const int ShowStateInStatus = 1;

    /// <summary>Discord refuses <c>details</c>, <c>state</c> and <c>large_text</c> shorter than 2 or longer than 128 characters.</summary>
    public const int MinTextLength = 2;

    /// <inheritdoc cref="MinTextLength"/>
    public const int MaxTextLength = 128;

    /// <summary>The longest image link sent as <c>large_image</c>; a longer one is left out.</summary>
    public const int MaxImageUrlLength = 256;

    /// <summary>The cover is asked for at this size.</summary>
    public const int CoverPixels = 512;

    // Pads a one-character text to Discord's minimum without showing anything (it isn't whitespace, so it isn't trimmed).
    private const char Padding = '​';

    /// <summary>A song or video: title, artists, album, cover, and start/end times so Discord draws the progress bar.</summary>
    /// <param name="startedAt">When the song would have started had it played without pauses: now minus the position.</param>
    /// <param name="duration">The length, or null when unknown (then only the start is sent).</param>
    public static JsonObject ForSong(Track track, DateTimeOffset startedAt, TimeSpan? duration)
    {
        ArgumentNullException.ThrowIfNull(track);
        var timestamps = new JsonObject { ["start"] = startedAt.ToUnixTimeMilliseconds() };
        if (duration is { } length && length > TimeSpan.Zero)
        {
            timestamps["end"] = (startedAt + length).ToUnixTimeMilliseconds();
        }

        return Build(Text(track.Title), Text(track.ArtistsText), timestamps, ImageUrl(CoverUrl(track)), Text(track.Album?.Name));
    }

    /// <summary>
    /// A live station: the song on air (or the station while it is unknown), the station as the status line, the time
    /// since the station started and no end.
    /// </summary>
    public static JsonObject ForStation(RadioStation station, RadioNowPlaying? onAir, DateTimeOffset startedAt, string? coverUrl)
    {
        ArgumentNullException.ThrowIfNull(station);
        var song = onAir is not null && onAir.StationId == station.Id ? SongLine(onAir) : null;
        var name = Text(station.Name);
        var timestamps = new JsonObject { ["start"] = startedAt.ToUnixTimeMilliseconds() };
        return Build(song ?? name, name, timestamps, ImageUrl(coverUrl), name);
    }

    /// <summary>
    /// <paramref name="value"/> trimmed and fitted to Discord's 2 to 128 characters: cut with an ellipsis when longer (never
    /// inside a character), padded with an invisible character when shorter. Null for an empty text.
    /// </summary>
    public static string? Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = Whitespace().Replace(value.Trim(), " ");
        if (text.Length > MaxTextLength)
        {
            var cut = 0;
            var elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext() && elements.ElementIndex + elements.GetTextElement().Length <= MaxTextLength - 1)
            {
                cut = elements.ElementIndex + elements.GetTextElement().Length;
            }

            text = text[..cut].TrimEnd() + "…";
        }

        return text.Length < MinTextLength ? text.PadRight(MinTextLength, Padding) : text;
    }

    /// <summary>An absolute https link short enough for <c>large_image</c>, else null.</summary>
    public static string? ImageUrl(string? url) =>
        url is { Length: > 0 and <= MaxImageUrlLength }
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
            ? url
            : null;

    /// <summary>
    /// The track's cover at about <see cref="CoverPixels"/>: Google's image host serves any size through the "=wN-hN"
    /// suffix (as Now Playing asks for big art), other hosts the listed size closest to it.
    /// </summary>
    public static string? CoverUrl(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var url = track.ThumbnailFor(CoverPixels)?.Url;
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !(uri.Host.EndsWith("googleusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("ggpht.com", StringComparison.OrdinalIgnoreCase)))
        {
            return url;
        }

        return SizeSuffix().Replace(url, $"=w{CoverPixels}-h{CoverPixels}", 1);
    }

    private static string? SongLine(RadioNowPlaying song)
    {
        var artist = string.IsNullOrWhiteSpace(song.Artist) ? null : song.Artist.Trim();
        return Text(artist is null ? song.Title : $"{artist} - {song.Title}");
    }

    private static JsonObject Build(string? details, string? state, JsonObject timestamps, string? image, string? imageText)
    {
        var activity = new JsonObject
        {
            ["type"] = ListeningType,
            ["status_display_type"] = ShowStateInStatus,
        };

        if (details is not null)
        {
            activity["details"] = details;
        }

        if (state is not null)
        {
            activity["state"] = state;
        }

        activity["timestamps"] = timestamps;
        if (image is not null)
        {
            var assets = new JsonObject { ["large_image"] = image };
            if (imageText is not null)
            {
                assets["large_text"] = imageText;
            }

            activity["assets"] = assets;
        }

        return activity;
    }

    [GeneratedRegex(@"=w\d+-h\d+")]
    private static partial Regex SizeSuffix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
