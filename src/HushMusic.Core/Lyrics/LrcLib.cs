using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HushMusic.Core.Lyrics;

/// <summary>One LRCLIB lyrics record (<c>/api/get</c> answers one, <c>/api/search</c> a list).</summary>
public sealed record LrcLibRecord(long Id, string TrackName, string ArtistName, string? AlbumName, TimeSpan? Duration)
{
    public bool Instrumental { get; init; }

    public string? PlainLyrics { get; init; }

    /// <summary>LRC, possibly with enhanced word tags.</summary>
    public string? SyncedLyrics { get; init; }

    public bool HasSynced => !string.IsNullOrWhiteSpace(SyncedLyrics);
}

/// <summary>LRCLIB JSON, matching search results to a track, and turning a record into lyrics documents.</summary>
public static class LrcLib
{
    /// <summary>A result whose length differs from the track's by more than this is another recording.</summary>
    public static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(3);

    /// <summary>Fewer non-empty lines than this is not lyrics (LRCLIB has a few placeholder entries such as "probe").</summary>
    public const int MinimumLines = 2;

    public const string ProviderName = "LRCLIB";

    /// <summary>One record; null when the JSON is not one.</summary>
    public static LrcLibRecord? ParseRecord(JsonNode? node)
    {
        if (node is not JsonObject json || Long(json["id"]) is not { } id)
        {
            return null;
        }

        var trackName = Text(json["trackName"]) ?? Text(json["name"]);
        var artistName = Text(json["artistName"]);
        if (trackName is null || artistName is null)
        {
            return null;
        }

        return new LrcLibRecord(
            id,
            trackName,
            artistName,
            Text(json["albumName"]),
            Double(json["duration"]) is { } seconds && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null)
        {
            Instrumental = json["instrumental"] is JsonValue flag && flag.TryGetValue<bool>(out var instrumental) && instrumental,
            PlainLyrics = Text(json["plainLyrics"]),
            SyncedLyrics = Text(json["syncedLyrics"]),
        };
    }

    /// <summary>A <c>/api/search</c> answer: the records it could read.</summary>
    public static IReadOnlyList<LrcLibRecord> ParseSearch(string json)
    {
        try
        {
            return JsonNode.Parse(json) is JsonArray array
                ? [.. array.Select(ParseRecord).OfType<LrcLibRecord>()]
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The candidate that is this track: its length within <see cref="DurationTolerance"/> (when the track's is known;
    /// otherwise the title must match exactly), a similar title and the same artist, real lyrics or the instrumental
    /// flag. Among those, an exact title wins, then word-synced over synced over plain over instrumental, then the
    /// closest length.
    /// </summary>
    public static LrcLibRecord? PickBest(IEnumerable<LrcLibRecord> candidates, LyricsQuery query)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(query);
        var wanted = LyricsText.Normalize(LyricsText.CoreTitle(query.Title));
        LrcLibRecord? best = null;
        (int Exact, int Lyrics, double Title, double Distance) bestScore = default;
        foreach (var candidate in candidates)
        {
            var distance = query.Duration is { } duration && candidate.Duration is { } length
                ? Math.Abs((length - duration).TotalSeconds)
                : double.NaN;
            if (query.Duration is not null && (double.IsNaN(distance) || distance > DurationTolerance.TotalSeconds))
            {
                continue;
            }

            var title = LyricsText.Normalize(LyricsText.CoreTitle(candidate.TrackName));
            var exact = title == wanted;
            var similarity = exact ? 1 : LyricsText.Similarity(title, wanted);
            if (similarity < 0.6 || (query.Duration is null && !exact) || !LyricsText.SameArtist(candidate.ArtistName, query.Artist))
            {
                continue;
            }

            var rank = LyricsRank(candidate);
            if (rank < 0)
            {
                continue;
            }

            var score = (exact ? 1 : 0, rank, similarity, double.IsNaN(distance) ? 0 : -distance);
            if (best is null || score.CompareTo(bestScore) > 0)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>3 word-synced, 2 synced, 1 plain, 0 instrumental, -1 nothing usable.</summary>
    public static int LyricsRank(LrcLibRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Instrumental)
        {
            return 0;
        }

        if (record.HasSynced)
        {
            var lrc = LrcParser.Parse(record.SyncedLyrics);
            if (lrc.Lines.Count(l => LyricsCleanup.CleanText(l.Text, LyricsTextKind.Lyrics).Length > 0) >= MinimumLines)
            {
                return lrc.HasWordTiming ? 3 : 2;
            }
        }

        return LyricsCleanup.Plain(record.PlainLyrics).Count(l => l.Text.Length > 0) >= MinimumLines ? 1 : -1;
    }

    /// <summary>
    /// The documents a record fills: <see cref="LyricsSources.LrcLibWord"/> when its LRC has word tags,
    /// <see cref="LyricsSources.LrcLibSynced"/> for its LRC (lines only), <see cref="LyricsSources.LrcLibPlain"/> for its
    /// plain text (or the LRC's text when it has none). An instrumental record fills the synced and plain entries with
    /// an empty, instrumental document.
    /// </summary>
    public static IReadOnlyList<LyricsDocument> ToDocuments(LrcLibRecord record, TimeSpan? songEnd)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Instrumental)
        {
            return
            [
                new LyricsDocument(LyricsSources.LrcLibSynced, ProviderName, LyricsSyncLevel.Line, []) { IsInstrumental = true },
                new LyricsDocument(LyricsSources.LrcLibPlain, ProviderName, LyricsSyncLevel.Unsynced, []) { IsInstrumental = true },
            ];
        }

        var documents = new List<LyricsDocument>(3);
        IReadOnlyList<LyricLine> synced = [];
        if (record.HasSynced)
        {
            var lrc = LrcParser.Parse(record.SyncedLyrics);
            synced = LyricsCleanup.Timed(lrc.Lines, songEnd ?? record.Duration);
            if (synced.Count < MinimumLines)
            {
                synced = [];
            }
        }

        var plainText = !string.IsNullOrWhiteSpace(record.PlainLyrics) ? record.PlainLyrics : LyricsCleanup.ToPlainText(synced);
        var plain = LyricsCleanup.Plain(plainText);
        var writers = LyricsCleanup.FindWriters(
            (record.PlainLyrics ?? string.Empty).ReplaceLineEndings("\n").Split('\n')
                .Concat(LrcParser.Parse(record.SyncedLyrics).Lines.Select(l => l.Text)));

        if (synced.Count > 0)
        {
            if (synced.Any(l => l.Words.Count > 0))
            {
                documents.Add(new LyricsDocument(LyricsSources.LrcLibWord, ProviderName, LyricsSyncLevel.Word, synced) { Writers = writers });
            }

            documents.Add(new LyricsDocument(
                LyricsSources.LrcLibSynced,
                ProviderName,
                LyricsSyncLevel.Line,
                [.. synced.Select(l => l.Words.Count == 0 ? l : l with { Words = [] })])
            { Writers = writers });
        }

        if (plain.Count(l => l.Text.Length > 0) >= MinimumLines)
        {
            documents.Add(new LyricsDocument(LyricsSources.LrcLibPlain, ProviderName, LyricsSyncLevel.Unsynced, plain) { Writers = writers });
        }

        return documents;
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static long? Long(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<long>(out var number) ? number
        : value.TryGetValue<double>(out var real) ? (long)real
        : value.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed
        : null
        : null;

    private static double? Double(JsonNode? node) => node is JsonValue value
        ? value.TryGetValue<double>(out var number) ? number
        : value.TryGetValue<string>(out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed
        : null
        : null;
}
