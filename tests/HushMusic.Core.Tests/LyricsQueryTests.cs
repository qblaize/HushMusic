using HushMusic.Core.Lyrics;
using HushMusic.Core.Models;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class LyricsQueryTests
{
    [Theory]
    [InlineData("Rick Astley - Never Gonna Give You Up (Official Music Video)", "Rick Astley", "Never Gonna Give You Up")]
    [InlineData("Blinding Lights (Official Video)", "The Weeknd", "Blinding Lights")]
    [InlineData("Song [Lyrics]", null, "Song")]
    [InlineData("Song (Lyric Video) [HD]", null, "Song")]
    [InlineData("Song (feat. Someone Else)", null, "Song")]
    [InlineData("Song [ft. Someone]", null, "Song")]
    [InlineData("Song ft. Someone", null, "Song")]
    [InlineData("Song (with Someone)", null, "Song")]
    [InlineData("Song | Official Audio", null, "Song")]
    [InlineData("\"Song\"", null, "Song")]
    [InlineData("Bohemian Rhapsody (Remastered 2011)", "Queen", "Bohemian Rhapsody (Remastered 2011)")]
    [InlineData("Other Band - Song", "Rick Astley", "Other Band - Song")]
    [InlineData("Despacito", "Luis Fonsi", "Despacito")]
    public void Titles_lose_video_noise_but_keep_version_notes(string title, string? artist, string expected)
    {
        Assert.Equal(expected, LyricsText.CleanTitle(title, artist));
    }

    [Theory]
    [InlineData("Bohemian Rhapsody (Remastered 2011)", "Bohemian Rhapsody")]
    [InlineData("Hello - Live", "Hello")]
    [InlineData("Song (Official Video) [Remix]", "Song")]
    [InlineData("(Untitled)", "(Untitled)")]
    public void The_core_title_drops_every_version_note(string title, string expected)
    {
        Assert.Equal(expected, LyricsText.CoreTitle(title));
    }

    [Theory]
    [InlineData("Rick Astley - Topic", "Rick Astley")]
    [InlineData("RickAstleyVEVO", "RickAstley")]
    [InlineData("  The   Weeknd ", "The Weeknd")]
    [InlineData("VEVO", "VEVO")]
    public void Channel_suffixes_are_removed_from_artists(string artist, string expected)
    {
        Assert.Equal(expected, LyricsText.CleanArtist(artist));
    }

    [Fact]
    public void A_track_becomes_a_query_with_its_first_artist()
    {
        var track = new Track
        {
            Title = "Despacito (feat. Daddy Yankee)",
            VideoId = "kJQP7kiw5Fk",
            Artists = [new ArtistRef("Luis Fonsi", "UC1"), new ArtistRef("Daddy Yankee", "UC2")],
            Album = new AlbumRef("VIDA", "MPREb_1"),
            Duration = TimeSpan.FromSeconds(229),
        };

        Assert.Equal(new LyricsQuery("Despacito", "Luis Fonsi", "VIDA", TimeSpan.FromSeconds(229)), LyricsQuery.From(track));
        Assert.Null(LyricsQuery.From(track with { Artists = [] }));
    }

    [Fact]
    public void Similarity_and_artist_matching()
    {
        Assert.Equal(1, LyricsText.Similarity("Blinding Lights", "blinding lights!"));
        Assert.True(LyricsText.Similarity("Blinding Lights", "Blinding Light") > 0.8);
        Assert.True(LyricsText.Similarity("Blinding Lights", "Bohemian Rhapsody") < 0.3);
        Assert.Equal("beyonce and jay z", LyricsText.Normalize("Beyoncé & JAY-Z"));

        Assert.True(LyricsText.SameArtist("Luis Fonsi, Daddy Yankee", "Luis Fonsi"));
        Assert.True(LyricsText.SameArtist("The Weeknd", "Weeknd"));
        Assert.True(LyricsText.SameArtist("Rick Astley", "Rick Astley - Topic"));
        Assert.False(LyricsText.SameArtist("Queens of the Stone Age", "Queen"));
        Assert.False(LyricsText.SameArtist("", "Queen"));
    }
}
