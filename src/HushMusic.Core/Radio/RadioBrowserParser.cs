using System.Text.Json;
using System.Text.Json.Nodes;
using HushMusic.Core.Models;

namespace HushMusic.Core.Radio;

/// <summary>
/// Maps Radio Browser station lists (<c>/json/stations/…</c>) to <see cref="RadioStation"/>s, keeping only what Windows
/// MediaPlayer can play: MP3 or AAC(+) over HTTP(S), not HLS, working at the directory's last check. Duplicates (same name
/// or same stream) are dropped, keeping the first (the API returns the most played first).
/// </summary>
public static class RadioBrowserParser
{
    private static readonly HashSet<string> PlayableCodecs = new(StringComparer.OrdinalIgnoreCase) { "MP3", "AAC", "AAC+" };

    // Playlist files: url_resolved normally points past them, but not always.
    private static readonly string[] PlaylistExtensions = [".m3u", ".m3u8", ".pls", ".asx", ".xspf"];

    public static IReadOnlyList<RadioStation> ParseStations(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonArray? array;
        try
        {
            array = JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException ex)
        {
            throw new HushException("The radio directory sent an unreadable answer.", ex);
        }

        if (array is null)
        {
            return [];
        }

        var stations = new List<RadioStation>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var streams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in array)
        {
            if (node is not JsonObject item || ToStation(item) is not { } station)
            {
                continue;
            }

            if (names.Add(NameKey(station.Name)) & streams.Add(StreamKey(station.StreamUrl)))
            {
                stations.Add(station);
            }
        }

        return stations;
    }

    /// <summary>Merges several result lists round-robin (so every tag is represented near the top), without duplicates.</summary>
    public static IReadOnlyList<RadioStation> Interleave(IReadOnlyList<IReadOnlyList<RadioStation>> lists, int limit)
    {
        ArgumentNullException.ThrowIfNull(lists);
        var result = new List<RadioStation>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var streams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; result.Count < limit && lists.Any(l => i < l.Count); i++)
        {
            foreach (var list in lists)
            {
                if (i < list.Count && result.Count < limit)
                {
                    var station = list[i];
                    if (ids.Add(station.Id) & names.Add(NameKey(station.Name)) & streams.Add(StreamKey(station.StreamUrl)))
                    {
                        result.Add(station);
                    }
                }
            }
        }

        return result;
    }

    private static RadioStation? ToStation(JsonObject item)
    {
        var id = Text(item, "stationuuid");
        var name = Text(item, "name");
        var url = HttpUrl(Text(item, "url_resolved")) ?? HttpUrl(Text(item, "url"));
        var codec = Text(item, "codec");
        if (id is null || name is null || url is null || codec is null || !PlayableCodecs.Contains(codec)
            || Number(item, "lastcheckok") != 1 || Number(item, "hls") == 1 || IsPlaylist(url))
        {
            return null;
        }

        var bitrate = Number(item, "bitrate");
        return new RadioStation
        {
            Id = id,
            Name = name,
            StreamUrl = url,
            Homepage = HttpUrl(Text(item, "homepage")),
            LogoUrl = HttpUrl(Text(item, "favicon")),
            Tags = Tags(Text(item, "tags")),
            Country = CountryName(Text(item, "country")),
            CountryCode = Text(item, "countrycode")?.ToUpperInvariant(),
            Codec = codec.ToUpperInvariant(),
            BitrateKbps = bitrate is > 0 and < 2000 ? bitrate : null,
        };
    }

    // The directory uses ISO's long forms ("The United States Of America").
    private static string? CountryName(string? country) => country switch
    {
        null => null,
        "The United States Of America" => "United States",
        "The United Kingdom Of Great Britain And Northern Ireland" => "United Kingdom",
        "The Russian Federation" => "Russia",
        _ when country.StartsWith("The ", StringComparison.Ordinal) => country[4..],
        _ => country,
    };

    private static IReadOnlyList<string> Tags(string? tags) =>
        tags is null
            ? []
            : [.. tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => t.Length is > 1 and <= 30)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)];

    private static string? Text(JsonObject item, string key)
    {
        try
        {
            return item[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static int? Number(JsonObject item, string key)
    {
        if (item[key] is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out number) ? number : null;
    }

    private static string? HttpUrl(string? url) =>
        url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? url
            : null;

    private static bool IsPlaylist(string url)
    {
        var path = new Uri(url).AbsolutePath;
        return PlaylistExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    private static string NameKey(string name) => string.Join(' ', name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string StreamKey(string url) => url.Trim().TrimEnd('/');
}
