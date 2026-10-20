namespace HushMusic.Core.Lyrics;

/// <summary>The lyrics source entries (one per source and sync level) and the user's order of them.</summary>
public static class LyricsSources
{
    public const string YouTubeSynced = "youtube-synced";

    public const string YouTubeCaptions = "ytcaptions-synced";

    public const string LrcLibWord = "lrclib-word";

    public const string LrcLibSynced = "lrclib-synced";

    public const string YouTubePlain = "youtube-plain";

    public const string LrcLibPlain = "lrclib-plain";

    /// <summary>Every entry, best first: the default order.</summary>
    public static IReadOnlyList<LyricsSourceInfo> All { get; } =
    [
        new(YouTubeSynced, "YouTube", LyricsSyncLevel.Line),
        new(YouTubeCaptions, "YouTube captions", LyricsSyncLevel.Line),
        new(LrcLibWord, "LRCLIB", LyricsSyncLevel.Word),
        new(LrcLibSynced, "LRCLIB", LyricsSyncLevel.Line),
        new(YouTubePlain, "YouTube", LyricsSyncLevel.Unsynced),
        new(LrcLibPlain, "LRCLIB", LyricsSyncLevel.Unsynced),
    ];

    public static LyricsSourceInfo? Find(string? key) => All.FirstOrDefault(s => s.Key == key);

    /// <summary>
    /// The saved order made whole: unknown and repeated keys are dropped, and every entry that is missing (added since
    /// the order was saved) is enabled and slotted in right after the nearest entry that precedes it in the default
    /// order, or before the nearest that follows it (Better Lyrics' <c>mergePreferredProviders</c>).
    /// </summary>
    public static IReadOnlyList<LyricsSourceSetting> Merge(IEnumerable<LyricsSourceSetting>? saved)
    {
        var merged = new List<LyricsSourceSetting>(All.Count);
        foreach (var setting in saved ?? [])
        {
            if (setting is not null && Find(setting.Key) is not null && merged.All(m => m.Key != setting.Key))
            {
                merged.Add(setting);
            }
        }

        for (var defaultIndex = 0; defaultIndex < All.Count; defaultIndex++)
        {
            var key = All[defaultIndex].Key;
            if (IndexOf(merged, key) >= 0)
            {
                continue;
            }

            var insertAt = -1;
            for (var i = defaultIndex - 1; i >= 0 && insertAt < 0; i--)
            {
                var predecessor = IndexOf(merged, All[i].Key);
                if (predecessor >= 0)
                {
                    insertAt = predecessor + 1;
                }
            }

            for (var i = defaultIndex + 1; i < All.Count && insertAt < 0; i++)
            {
                insertAt = IndexOf(merged, All[i].Key);
            }

            merged.Insert(insertAt < 0 ? merged.Count : insertAt, new LyricsSourceSetting(key, true));
        }

        return merged;
    }

    /// <summary>True when both lists hold the same entries in the same order with the same switches.</summary>
    public static bool SameOrder(IReadOnlyList<LyricsSourceSetting> a, IReadOnlyList<LyricsSourceSetting> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => pair.First == pair.Second);

    /// <summary>The provider behind an entry; one request to it answers all of its entries.</summary>
    internal static LyricsProviderId ProviderOf(string key) => key switch
    {
        YouTubeSynced or YouTubePlain => LyricsProviderId.YouTube,
        YouTubeCaptions => LyricsProviderId.YouTubeCaptions,
        _ => LyricsProviderId.LrcLib,
    };

    private static int IndexOf(List<LyricsSourceSetting> list, string key) => list.FindIndex(s => s.Key == key);
}

internal enum LyricsProviderId
{
    YouTube,
    YouTubeCaptions,
    LrcLib,
}
