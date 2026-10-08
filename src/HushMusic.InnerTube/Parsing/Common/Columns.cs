using System.Text.Json.Nodes;

namespace HushMusic.InnerTube.Parsing.Common;

/// <summary>Columns of a <c>musicResponsiveListItemRenderer</c> row (ytmusicapi get_flex_column_item / get_item_text).</summary>
internal static class Columns
{
    public static int FlexCount(JsonNode? item) => item.Arr("flexColumns")?.Count ?? 0;

    /// <summary>
    /// Flex column <paramref name="index"/>, or null when it is out of range or has no runs
    /// (greyed-out rows have <c>"text": {}</c>).
    /// </summary>
    public static JsonObject? Flex(JsonNode? item, int index)
    {
        var column = item.Obj("flexColumns", index, "musicResponsiveListItemFlexColumnRenderer");
        return column.Arr("text", "runs") is null ? null : column;
    }

    public static JsonArray? FlexRuns(JsonNode? item, int index) => Flex(item, index).Arr("text", "runs");

    public static JsonObject? FlexRun(JsonNode? item, int index, int runIndex = 0) => Flex(item, index).Obj("text", "runs", runIndex);

    public static string? FlexText(JsonNode? item, int index, int runIndex = 0) => FlexRun(item, index, runIndex).Str("text");

    /// <summary>Fixed column text: <c>.text.simpleText</c> or <c>.text.runs[0].text</c> (durations on album/playlist rows).</summary>
    public static string? FixedText(JsonNode? item, int index)
    {
        var text = item.Obj("fixedColumns", index, "musicResponsiveListItemFixedColumnRenderer", "text");
        return text.Str("simpleText") ?? text.Str("runs", 0, "text");
    }
}
