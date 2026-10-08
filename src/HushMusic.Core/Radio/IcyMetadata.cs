using System.Text;
using HushMusic.Core.Models;

namespace HushMusic.Core.Radio;

/// <summary>
/// Parses Icecast/Shoutcast ICY metadata blocks: <c>StreamTitle='Artist - Title';StreamUrl='…';</c>, padded with NULs.
/// </summary>
/// <remarks>
/// There is no escaping in the format: titles contain apostrophes ("I'll Erase You", "Catherine Duc 'Daydream' Remix"), so a
/// value ends at the first <c>';</c> (or at the last quote of the block). Most servers send UTF-8; older ones send
/// Latin-1, which is the fallback when the bytes are not valid UTF-8.
/// </remarks>
public static class IcyMetadata
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // "Artist - Title"; some stations use an en or em dash instead of the hyphen.
    private static readonly string[] Separators = [" - ", " – ", " — "];

    /// <summary>Decodes a metadata block (UTF-8, else Latin-1) and strips the NUL padding.</summary>
    public static string Decode(ReadOnlySpan<byte> block)
    {
        var end = block.IndexOf((byte)0);
        if (end >= 0)
        {
            block = block[..end];
        }

        try
        {
            return StrictUtf8.GetString(block);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(block);
        }
    }

    /// <summary>The value of <paramref name="key"/> (e.g. "StreamTitle"), or null when the block has no such field.</summary>
    public static string? GetField(string metadata, string key)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var marker = key + "='";
        var start = metadata.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }

        start += marker.Length;
        var end = metadata.IndexOf("';", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = metadata.LastIndexOf('\'');
            if (end < start)
            {
                end = metadata.Length;
            }
        }

        return metadata[start..end].Trim();
    }

    /// <summary>
    /// The song in a stream title, or null when there is none: empty titles, and titles that only repeat the station's
    /// name ("sunshine live - Tech House", "FluxFM - Livestream", "ChillHop" on FluxFM Chillhop).
    /// </summary>
    public static RadioNowPlaying? ToNowPlaying(RadioStation station, string? streamTitle, string? streamUrl = null)
    {
        ArgumentNullException.ThrowIfNull(station);
        var title = streamTitle?.Trim();
        if (string.IsNullOrEmpty(title) || IsStationIdent(title, station.Name))
        {
            return null;
        }

        string? artist = null;
        var song = title;
        var separator = FindSeparator(title);
        if (separator > 0)
        {
            artist = title[..separator].Trim();
            song = title[(separator + 3)..].Trim();
            if (song.Length == 0)
            {
                song = artist;
                artist = null;
            }
            else if (artist.Length == 0)
            {
                artist = null;
            }
        }

        return new RadioNowPlaying(station.Id, title, artist, song, ImageUrl(streamUrl));
    }

    /// <summary>True when the stream title is just the station identifying itself rather than a song.</summary>
    public static bool IsStationIdent(string streamTitle, string stationName)
    {
        var title = Normalize(streamTitle);
        var station = Normalize(stationName);
        if (title.Length == 0)
        {
            return true;
        }

        if (station.Length == 0)
        {
            return false;
        }

        if (title == station || (title.Length >= 4 && station.Contains(title, StringComparison.Ordinal)) || (station.Length >= 4 && title.StartsWith(station, StringComparison.Ordinal)))
        {
            return true;
        }

        var separator = FindSeparator(streamTitle);
        if (separator > 0)
        {
            // "SUNSHINE LIVE - Chillout", "Radio Swiss Jazz - www.radioswissjazz.ch": the "artist" is the station.
            var artist = Normalize(streamTitle[..separator]);
            return artist.Length >= 4 && (station.StartsWith(artist, StringComparison.Ordinal) || artist.StartsWith(station, StringComparison.Ordinal));
        }

        // No "artist - title" shape: a long shared prefix with the station name ("bigFM Deep Tech House" on "bigFM House Beats").
        return CommonPrefixLength(title, station) >= 5;
    }

    /// <summary>Index of the first " - " (or en/em dash) separator; all are three characters long. -1 when there is none.</summary>
    private static int FindSeparator(string title)
    {
        var best = -1;
        foreach (var separator in Separators)
        {
            var index = title.IndexOf(separator, StringComparison.Ordinal);
            if (index > 0 && (best < 0 || index < best))
            {
                best = index;
            }
        }

        return best;
    }

    private static string? ImageUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var path = uri.AbsolutePath;
        return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    private static string Normalize(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var length = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < length && a[i] == b[i])
        {
            i++;
        }

        return i;
    }
}
