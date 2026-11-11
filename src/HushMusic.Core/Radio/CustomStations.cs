using System.Text.RegularExpressions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Radio;

/// <summary>What a stream link turned out to be (<see cref="Abstractions.IStationProbe"/>).</summary>
/// <param name="StreamUrl">The audio stream itself (a playlist link is replaced by the stream in it).</param>
/// <param name="Name">The station's own name (ICY <c>icy-name</c>), when it sends one.</param>
/// <param name="Genre">ICY <c>icy-genre</c>.</param>
/// <param name="Codec">"MP3", "AAC", "AAC+", "OGG", "Opus" or "FLAC", from the content type.</param>
/// <param name="BitrateKbps">ICY <c>icy-br</c>.</param>
/// <param name="Homepage">ICY <c>icy-url</c>, http(s) only.</param>
public sealed record StationProbeResult(Uri StreamUrl, string? Name, string? Genre, string? Codec, int? BitrateKbps, string? Homepage);

/// <summary>What a server sent back for a stream link.</summary>
public enum StreamLinkKind
{
    /// <summary>Audio: plays as it is.</summary>
    Audio,

    /// <summary>A .pls / .m3u / .asx / .xspf playlist that names the stream.</summary>
    Playlist,

    /// <summary>A web page, not a stream.</summary>
    WebPage,
}

/// <summary>Stations the user adds by their stream link. They live in the favourites, like any other station.</summary>
public static partial class CustomStations
{
    /// <summary>Prefix of <see cref="RadioStation.Id"/> for stations added by hand.</summary>
    public const string IdPrefix = "custom-";

    public const int MaxNameLength = 100;

    private static readonly string[] PlaylistExtensions = [".pls", ".m3u", ".m3u8", ".asx", ".xspf"];

    private static readonly HashSet<string> PlaylistTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "audio/x-scpls", "audio/scpls", "application/pls+xml", "audio/x-mpegurl", "audio/mpegurl", "application/x-mpegurl",
        "application/vnd.apple.mpegurl", "video/x-ms-asf", "video/x-ms-asx", "application/xspf+xml",
    };

    public static bool IsCustom(RadioStation? station) => station?.Id.StartsWith(IdPrefix, StringComparison.Ordinal) == true;

    /// <summary>
    /// Reads a typed or pasted stream link: trims it and adds "http://" when there is no scheme
    /// ("stream.example.com:8000/live"). Only http and https.
    /// </summary>
    public static bool TryParseLink(string? text, out Uri link)
    {
        link = null!;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || string.IsNullOrEmpty(uri.Host))
        {
            return false;
        }

        link = uri;
        return true;
    }

    /// <summary>
    /// Sorts a response by its content type, falling back on the link's extension for servers that send playlists as
    /// text/plain or application/octet-stream.
    /// </summary>
    public static StreamLinkKind Classify(string? mediaType, Uri link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var type = mediaType?.Trim() ?? string.Empty;
        if (PlaylistTypes.Contains(type))
        {
            return StreamLinkKind.Playlist;
        }

        var path = link.AbsolutePath;
        var playlistLink = PlaylistExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
        if (type.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) || type.StartsWith("application/xhtml", StringComparison.OrdinalIgnoreCase))
        {
            return StreamLinkKind.WebPage;
        }

        if (playlistLink && (type.Length == 0 || type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) || type.EndsWith("xml", StringComparison.OrdinalIgnoreCase)))
        {
            return StreamLinkKind.Playlist;
        }

        return type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ? StreamLinkKind.WebPage : StreamLinkKind.Audio;
    }

    /// <summary>An HLS playlist (segments): Windows plays it as it is, so it isn't unpacked.</summary>
    public static bool IsHls(string playlist) => playlist?.Contains("#EXT-X-", StringComparison.Ordinal) == true;

    /// <summary>
    /// The first http(s) stream a playlist names: PLS <c>File1=</c>, ASX <c>href</c>, XSPF <c>&lt;location&gt;</c>, or the
    /// first entry of an M3U. Relative entries resolve against <paramref name="playlistUrl"/>. Null when there is none.
    /// </summary>
    public static Uri? FirstStreamIn(string playlist, Uri playlistUrl)
    {
        ArgumentNullException.ThrowIfNull(playlistUrl);
        if (string.IsNullOrWhiteSpace(playlist))
        {
            return null;
        }

        var candidates = new List<string>();
        candidates.AddRange(PlsEntry().Matches(playlist).Select(m => m.Groups["url"].Value));
        candidates.AddRange(AsxEntry().Matches(playlist).Select(m => m.Groups["url"].Value));
        candidates.AddRange(XspfEntry().Matches(playlist).Select(m => System.Net.WebUtility.HtmlDecode(m.Groups["url"].Value)));
        if (candidates.Count == 0 && !playlist.TrimStart().StartsWith('<'))
        {
            candidates.AddRange(playlist.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#') && !l.StartsWith('[') && !l.Contains('=', StringComparison.Ordinal)));
        }

        foreach (var candidate in candidates)
        {
            if (Uri.TryCreate(playlistUrl, candidate.Trim(), out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                return uri;
            }
        }

        return null;
    }

    /// <summary>The codec a content type names, as the directory writes it.</summary>
    public static string? CodecFor(string? mediaType) => mediaType?.Trim().ToLowerInvariant() switch
    {
        "audio/mpeg" or "audio/mp3" or "audio/mpeg3" => "MP3",
        "audio/aac" or "audio/x-aac" => "AAC",
        "audio/aacp" => "AAC+",
        "audio/ogg" or "application/ogg" or "audio/vorbis" => "OGG",
        "audio/opus" => "Opus",
        "audio/flac" or "audio/x-flac" => "FLAC",
        _ => null,
    };

    /// <summary>ICY <c>icy-br</c>: "128", or "128,128" from some servers.</summary>
    public static int? ParseBitrate(string? value)
    {
        var first = value?.Split(',', ';')[0].Trim();
        return int.TryParse(first, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var kbps) && kbps is > 0 and < 10_000
            ? kbps
            : null;
    }

    /// <summary>
    /// The station to save: the typed name, else the stream's own, else the server's host name. Genres become tags.
    /// </summary>
    public static RadioStation Create(string? typedName, StationProbeResult probe, Guid id)
    {
        ArgumentNullException.ThrowIfNull(probe);
        var name = new[] { typedName, Usable(probe.Name), probe.StreamUrl.Host }.First(n => !string.IsNullOrWhiteSpace(n))!.Trim();
        if (name.Length > MaxNameLength)
        {
            name = name[..MaxNameLength].TrimEnd();
        }

        // Stations often send their website without a scheme ("example.radio").
        var homepage = TryParseLink(probe.Homepage, out var site) ? site.AbsoluteUri : null;
        var tags = (Usable(probe.Genre) ?? string.Empty)
            .Split([',', ';', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant())
            .Distinct()
            .Take(3)
            .ToList();
        return new RadioStation
        {
            Id = IdPrefix + id.ToString("N"),
            Name = name,
            StreamUrl = probe.StreamUrl.AbsoluteUri,
            Homepage = homepage,
            Tags = tags,
            Codec = probe.Codec,
            BitrateKbps = probe.BitrateKbps,
        };
    }

    // Servers fill unset ICY fields with placeholders.
    private static string? Usable(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Equals("no name", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("unspecified", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("various", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("this is my server name", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("stream", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    [GeneratedRegex(@"^\s*File\d+\s*=\s*(?<url>\S+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PlsEntry();

    [GeneratedRegex(@"<ref\s+href\s*=\s*""(?<url>[^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AsxEntry();

    [GeneratedRegex(@"<location>\s*(?<url>[^<\s]+)\s*</location>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex XspfEntry();
}
