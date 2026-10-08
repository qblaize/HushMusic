using System.Text.Json.Nodes;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class SearchSuggestionsParserTests
{
    private readonly CapturingLogger _log = new();

    [Fact]
    public void Text_suggestions_in_order()
    {
        var suggestions = SearchSuggestionsParser.Parse(ParserFixtures.Load("search_suggestions.json"), _log);

        Assert.Equal(
            ["daft punk", "daft punk veridis quo", "daft punk get lucky", "daft punk giorgio moroder", "daft punk one more time", "daft punk around the world"],
            suggestions.Queries);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Entity_rows_are_parsed_as_search_results()
    {
        // ytmusicapi ignores the second section; it holds an artist, songs and a video.
        var items = SearchSuggestionsParser.Parse(ParserFixtures.Load("search_suggestions.json"), _log).Items;

        Assert.Equal(5, items.Count);
        var artist = Assert.IsType<Artist>(items[0]);
        Assert.Equal(("Daft Punk", "UCRr1xG_2WIDs18a6cIiCxeA"), (artist.Title, artist.BrowseId));

        var song = Assert.IsType<Track>(items[1]);
        Assert.Equal(("Veridis Quo", "qe8Q7mjxjig", TrackType.Song), (song.Title, song.VideoId, song.Type));
        Assert.Equal("Daft Punk", Assert.Single(song.Artists).Name);
        Assert.Equal("109M plays", song.Views);

        var getLucky = Assert.IsType<Track>(items[2]);
        Assert.Equal(["Daft Punk", "Pharrell Williams", "Nile Rodgers"], getLucky.Artists.Select(a => a.Name));
        Assert.Equal("Random Access Memories", getLucky.Album?.Name);

        var video = Assert.IsType<Track>(items[4]);
        Assert.Equal(("One More Time", "A2VpR8HahKc", TrackType.Video), (video.Title, video.VideoId, video.Type));
    }

    [Fact]
    public void Suggestion_without_text_is_skipped_with_a_warning()
    {
        var response = ParserFixtures.Load("search_suggestions.json");
        var renderer = response.At("contents", 0, "searchSuggestionsSectionRenderer", "contents", 0, "searchSuggestionRenderer").AsObject();
        renderer.Remove("navigationEndpoint");
        renderer.Remove("suggestion");

        var suggestions = SearchSuggestionsParser.Parse(response, _log);

        Assert.Equal(5, suggestions.Queries.Count);
        Assert.Equal("daft punk veridis quo", suggestions.Queries[0]);
        Assert.Contains(_log.Warnings, w => w.Contains("searchSuggestionRenderer", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_object_returns_no_suggestions()
    {
        var suggestions = SearchSuggestionsParser.Parse(new JsonObject(), _log);
        Assert.Empty(suggestions.Queries);
        Assert.Empty(suggestions.Items);
    }
}
