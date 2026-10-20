using HushMusic.Core.Lyrics;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class LyricsSourcesTests
{
    [Fact]
    public void The_default_order_lists_every_source_best_first()
    {
        Assert.Equal(
            ["youtube-synced", "ytcaptions-synced", "lrclib-word", "lrclib-synced", "youtube-plain", "lrclib-plain"],
            LyricsSources.All.Select(s => s.Key));
        Assert.Equal(LyricsSources.All.Select(s => new LyricsSourceSetting(s.Key, true)), LyricsSources.Merge(null));
        Assert.Equal(("LRCLIB", LyricsSyncLevel.Word), (LyricsSources.Find("lrclib-word")!.ProviderName, LyricsSources.Find("lrclib-word")!.Sync));
        Assert.Null(LyricsSources.Find("musixmatch"));
    }

    [Fact]
    public void Saved_entries_keep_their_place_and_new_ones_join_after_their_default_predecessor()
    {
        var merged = LyricsSources.Merge([new("lrclib-synced", false), new("youtube-synced", true)]);

        Assert.Equal(
            [
                new LyricsSourceSetting("lrclib-synced", false),
                new LyricsSourceSetting("youtube-plain", true),
                new LyricsSourceSetting("lrclib-plain", true),
                new LyricsSourceSetting("youtube-synced", true),
                new LyricsSourceSetting("ytcaptions-synced", true),
                new LyricsSourceSetting("lrclib-word", true),
            ],
            merged);
    }

    [Fact]
    public void Unknown_and_repeated_keys_are_dropped()
    {
        var merged = LyricsSources.Merge(
            [new("old-source", true), new("youtube-plain", false), new("youtube-plain", true), null!]);

        Assert.Equal(6, merged.Count);
        Assert.Equal(new LyricsSourceSetting("youtube-plain", false), merged.Single(s => s.Key == "youtube-plain"));
        Assert.DoesNotContain(merged, s => s.Key == "old-source");
    }

    [Fact]
    public void Same_order_compares_keys_order_and_switches()
    {
        var order = LyricsSources.Merge(null);

        Assert.True(LyricsSources.SameOrder(order, LyricsSources.Merge(null)));
        Assert.False(LyricsSources.SameOrder(order, [.. order.Reverse()]));
        Assert.False(LyricsSources.SameOrder(order, [order[0] with { Enabled = false }, .. order.Skip(1)]));
    }
}
