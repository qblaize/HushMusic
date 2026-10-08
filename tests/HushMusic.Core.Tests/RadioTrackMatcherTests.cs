using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Models;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class RadioSongQueryTests
{
    [Theory]
    [InlineData("Avicii", "Levels (Radio Edit)", "Avicii Levels")]
    [InlineData("Artist", "Title [Original Mix]", "Artist Title")]
    [InlineData("Artist", "Title (Extended Mix) [Defected]", "Artist Title")]
    [InlineData("Artist", "Title (2011 Remaster)", "Artist Title")]
    [InlineData("Artist", "Title (Remastered 2011)", "Artist Title")]
    [InlineData("Artist", "Title (Purple Disco Machine Remix)", "Artist Title (Purple Disco Machine Remix)")]
    [InlineData("Artist", "Title (Live)", "Artist Title (Live)")]
    [InlineData("Artist feat. Singer", "Title", "Artist feat. Singer Title")]
    [InlineData("Now playing: Daft Punk", "One More Time", "Daft Punk One More Time")]
    [InlineData("Artist", "Title | Deep House", "Artist Title")]
    [InlineData("Artist", "Title // www.example-radio.com", "Artist Title")]
    [InlineData("Artist", "Title - example.fm", "Artist Title")]
    [InlineData("  Artist  ", "  ★ “Title”  ★ ", "Artist Title")]
    [InlineData("Some Artist", "Some Artist", "Some Artist")]
    [InlineData("Various", "Crystal Stafford/breathe (Pc S", "Crystal Stafford breathe")]
    [InlineData("Various Artists", "Artist - Title", "Artist - Title")]
    [InlineData("Ian Pooley", "Me Leve (Feat. Rosanna & Zelia", "Ian Pooley Me Leve")]
    public void Builds_a_plain_search_text(string artist, string title, string expected) =>
        Assert.Equal(expected, RadioSongQuery.Build(artist, title));

    [Theory]
    [InlineData("Some Song Without Artist", "Some Song Without Artist")]
    [InlineData("Some Song (Radio Edit)", "Some Song")]
    [InlineData("[NEW] ", "")]
    public void Handles_titles_without_an_artist(string title, string expected) =>
        Assert.Equal(expected, RadioSongQuery.Build(null, title));

    [Fact]
    public void Removes_the_station_name()
    {
        Assert.Equal("Artist Title", RadioSongQuery.Build("Artist", "Title - Sunshine Live", "Sunshine Live"));

        // A short or partial match stays: "Kiss" by Prince on "Kiss FM".
        Assert.Equal("Prince Kiss", RadioSongQuery.Build("Prince", "Kiss", "Kiss FM"));
    }

    [Fact]
    public void Compares_names_without_case_punctuation_or_diacritics() =>
        Assert.Equal(RadioSongQuery.Comparable("Ștefan Bănică, Jr."), RadioSongQuery.Comparable("stefan banica jr"));
}

public sealed class RadioTrackMatcherTests
{
    private readonly RadioMatchFakeSearch _search = new();
    private readonly RadioTrackMatcher _matcher;

    public RadioTrackMatcherTests()
    {
        _matcher = new RadioTrackMatcher(_search, NullLogger<RadioTrackMatcher>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Searches_songs_for_the_cleaned_title_and_returns_the_top_three()
    {
        _search.Results[SearchFilter.Songs] = Results(Song("1", "Other"), Song("2", "Another"), Song("3", "Third"), Song("4", "Fourth"));

        var tracks = await _matcher.FindAsync(Heard("Artist", "Title (Radio Edit)"), cancellationToken: Ct);

        Assert.Equal([("Artist Title", SearchFilter.Songs)], _search.Calls);
        Assert.Equal(["1", "2", "3"], tracks.Select(t => t.VideoId));
    }

    [Fact]
    public async Task Moves_a_result_matching_title_and_artist_to_the_front()
    {
        _search.Results[SearchFilter.Songs] = Results(
            Song("cover", "Title", "Karaoke Band"),
            Song("other", "Something Else", "Artist"),
            Song("real", "Title (feat. Singer)", "Artist"));

        var tracks = await _matcher.FindAsync(Heard("Artist", "Title"), cancellationToken: Ct);

        Assert.Equal(["real", "cover", "other"], tracks.Select(t => t.VideoId));
    }

    [Fact]
    public async Task Skips_unavailable_results_and_duplicates()
    {
        _search.Results[SearchFilter.Songs] = Results(Song("1", "A") with { IsAvailable = false }, Song("2", "B"), Song("2", "B"));

        var tracks = await _matcher.FindAsync(Heard("Artist", "Title"), cancellationToken: Ct);

        Assert.Equal(["2"], tracks.Select(t => t.VideoId));
    }

    [Fact]
    public async Task Falls_back_to_all_results_when_no_song_matches()
    {
        _search.Results[SearchFilter.Songs] = new SearchResults();
        _search.Results[SearchFilter.All] = new SearchResults { TopResult = Song("video", "Title"), Sections = [new Shelf { Title = "", Items = [new Artist { Title = "Artist", BrowseId = "UC1" }] }] };

        var tracks = await _matcher.FindAsync(Heard(null, "Title"), cancellationToken: Ct);

        Assert.Equal([SearchFilter.Songs, SearchFilter.All], _search.Calls.Select(c => c.Filter));
        Assert.Equal(["video"], tracks.Select(t => t.VideoId));
    }

    [Fact]
    public async Task Returns_nothing_when_nothing_matches()
    {
        var tracks = await _matcher.FindAsync(Heard("Artist", "Title"), cancellationToken: Ct);

        Assert.Empty(tracks);
    }

    [Fact]
    public async Task Remembers_results_for_the_session()
    {
        _search.Results[SearchFilter.Songs] = Results(Song("1", "Title", "Artist"));

        await _matcher.FindAsync(Heard("Artist", "Title"), cancellationToken: Ct);
        var again = await _matcher.FindAsync(Heard("Artist", "Title (Radio Edit)"), cancellationToken: Ct);

        Assert.Single(_search.Calls);
        Assert.Equal(["1"], again.Select(t => t.VideoId));
    }

    [Fact]
    public async Task Does_not_search_for_an_empty_title()
    {
        var tracks = await _matcher.FindAsync(Heard(null, "[NEW]"), cancellationToken: Ct);

        Assert.Empty(tracks);
        Assert.Empty(_search.Calls);
    }

    [Fact]
    public async Task Lets_network_errors_through_and_does_not_remember_them()
    {
        _search.Failure = new HttpRequestException("offline");

        await Assert.ThrowsAsync<HttpRequestException>(() => _matcher.FindAsync(Heard("Artist", "Title"), cancellationToken: Ct));

        _search.Failure = null;
        _search.Results[SearchFilter.Songs] = Results(Song("1", "Title", "Artist"));
        var tracks = await _matcher.FindAsync(Heard("Artist", "Title"), cancellationToken: Ct);

        Assert.Equal(["1"], tracks.Select(t => t.VideoId));
    }

    private static RadioNowPlaying Heard(string? artist, string title) =>
        new("station-1", artist is null ? title : $"{artist} - {title}", artist, title, null);

    private static Track Song(string videoId, string title, string artist = "Someone") => new()
    {
        Title = title,
        VideoId = videoId,
        Artists = [new ArtistRef(artist, null)],
    };

    private static SearchResults Results(params Track[] tracks) =>
        new() { Sections = [new Shelf { Title = "Songs", Items = tracks }] };
}

internal sealed class RadioMatchFakeSearch : ISearchApi
{
    public Dictionary<SearchFilter, SearchResults> Results { get; } = [];

    public List<(string Query, SearchFilter Filter)> Calls { get; } = [];

    public Exception? Failure { get; set; }

    public Task<SearchResults> SearchAsync(string query, SearchFilter filter = SearchFilter.All, string? continuation = null, CancellationToken cancellationToken = default)
    {
        Calls.Add((query, filter));
        return Failure is { } failure
            ? Task.FromException<SearchResults>(failure)
            : Task.FromResult(Results.TryGetValue(filter, out var results) ? results : new SearchResults());
    }

    public Task<SearchSuggestions> GetSuggestionsAsync(string input, CancellationToken cancellationToken = default) =>
        Task.FromResult(SearchSuggestions.Empty);
}
