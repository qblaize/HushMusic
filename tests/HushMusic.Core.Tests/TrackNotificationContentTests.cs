using System.Xml.Linq;
using HushMusic.Core.Models;
using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class TrackNotificationContentTests
{
    private static readonly Track Song = new()
    {
        VideoId = "abc123",
        Title = "Midnight City",
        Artists = [new ArtistRef("M83", null)],
        Album = new AlbumRef("Hurry Up, We're Dreaming", null),
    };

    private static readonly RadioStation Station = new()
    {
        Id = "0f3c",
        Name = "Radio Paradise",
        StreamUrl = "https://stream.example/rp.mp3",
        Tags = ["eclectic"],
        Country = "United States",
    };

    [Fact]
    public void A_song_shows_title_artists_and_album()
    {
        var notification = TrackNotification.For(Song, imageUrl: "https://img.example/cover.jpg");

        Assert.Equal("abc123", notification.Key);
        Assert.Equal("Midnight City", notification.Title);
        Assert.Equal("M83", notification.Subtitle);
        Assert.Equal("Hurry Up, We're Dreaming", notification.Attribution);
        Assert.Equal("https://img.example/cover.jpg", notification.ImageUrl);
    }

    [Fact]
    public void A_station_without_a_known_song_shows_the_station()
    {
        var notification = TrackNotification.For(Station.ToTrack(), onAir: null, imageUrl: "https://logo.example/rp.png");

        Assert.Equal("Radio Paradise", notification.Title);
        Assert.Equal("Eclectic · United States", notification.Subtitle);
        Assert.Null(notification.Attribution);
        Assert.Equal("https://logo.example/rp.png", notification.ImageUrl);
    }

    [Fact]
    public void A_station_shows_the_song_on_air_and_a_new_song_is_a_new_notification()
    {
        var station = Station.ToTrack();
        var first = TrackNotification.For(station, new RadioNowPlaying("0f3c", "Air - La femme d'argent", "Air", "La femme d'argent", "https://art.example/1.jpg"), "https://logo.example/rp.png");
        var second = TrackNotification.For(station, new RadioNowPlaying("0f3c", "Moby - Porcelain", "Moby", "Porcelain", null), "https://logo.example/rp.png");

        Assert.Equal("La femme d'argent", first.Title);
        Assert.Equal("Air · Radio Paradise", first.Subtitle);
        Assert.Equal("https://art.example/1.jpg", first.ImageUrl);
        Assert.Equal("https://logo.example/rp.png", second.ImageUrl);
        Assert.NotEqual(first.Key, second.Key);
    }

    [Fact]
    public void The_song_of_another_station_is_ignored()
    {
        var notification = TrackNotification.For(Station.ToTrack(), new RadioNowPlaying("other", "A - B", "A", "B", null));

        Assert.Equal("Radio Paradise", notification.Title);
    }

    [Fact]
    public void The_toast_has_the_texts_the_cover_a_next_button_and_no_sound()
    {
        var xml = TrackNotificationContent.Build(TrackNotification.For(Song), @"C:\Users\Some One\cache\cover 1.png", nextButton: true);
        var toast = XElement.Parse(xml);

        Assert.Equal(TrackNotificationContent.ShowArgument, (string?)toast.Attribute("launch"));
        var binding = toast.Element("visual")!.Element("binding")!;
        Assert.Equal("ToastGeneric", (string?)binding.Attribute("template"));
        var texts = binding.Elements("text").ToList();
        Assert.Equal(["Midnight City", "M83", "Hurry Up, We're Dreaming"], texts.Select(t => t.Value));
        Assert.Equal("attribution", (string?)texts[2].Attribute("placement"));

        var image = binding.Element("image")!;
        Assert.Equal("appLogoOverride", (string?)image.Attribute("placement"));
        Assert.Equal("file:///C:/Users/Some%20One/cache/cover%201.png", (string?)image.Attribute("src"));

        var action = toast.Element("actions")!.Element("action")!;
        Assert.Equal("Next", (string?)action.Attribute("content"));
        Assert.Equal(TrackNotificationContent.NextArgument, (string?)action.Attribute("arguments"));
        Assert.Equal("true", (string?)toast.Element("audio")!.Attribute("silent"));
    }

    [Fact]
    public void Text_is_escaped()
    {
        var notification = new TrackNotification("k", "Rock & <Roll>", "\"Quotes\" & 'apostrophes'", null, null);

        var toast = XElement.Parse(TrackNotificationContent.Build(notification, null, nextButton: false));

        var texts = toast.Element("visual")!.Element("binding")!.Elements("text").Select(t => t.Value);
        Assert.Equal(["Rock & <Roll>", "\"Quotes\" & 'apostrophes'"], texts);
    }

    [Fact]
    public void Without_a_cover_or_next_there_is_no_image_or_button()
    {
        var toast = XElement.Parse(TrackNotificationContent.Build(TrackNotification.For(Song), null, nextButton: false));

        Assert.Null(toast.Element("visual")!.Element("binding")!.Element("image"));
        Assert.Null(toast.Element("actions"));
    }

    [Fact]
    public void A_relative_image_path_is_left_out()
    {
        var toast = XElement.Parse(TrackNotificationContent.Build(TrackNotification.For(Song), "cover.png", nextButton: false));

        Assert.Null(toast.Element("visual")!.Element("binding")!.Element("image"));
    }

    [Theory]
    [InlineData("action=next", TrackNotificationCommand.Next)]
    [InlineData("action=show", TrackNotificationCommand.Show)]
    [InlineData("", TrackNotificationCommand.Show)]
    [InlineData(null, TrackNotificationCommand.Show)]
    [InlineData("action=delete", TrackNotificationCommand.None)]
    public void Arguments_map_to_commands(string? arguments, TrackNotificationCommand expected) =>
        Assert.Equal(expected, TrackNotificationContent.Parse(arguments));
}
