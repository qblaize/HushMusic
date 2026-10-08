using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class PlayerParserTests
{
    private static readonly object[] AudioConfig = ["playerConfig", "audioConfig"];

    private readonly CapturingLogger _log = new();

    [Fact]
    public void Loudness_comes_from_audioConfig_loudnessDb()
    {
        var loudness = PlayerParser.ParseLoudnessDb(ParserFixtures.Load("player.json"), _log);

        Assert.NotNull(loudness);
        Assert.Equal(-1.0799999, loudness.Value, 7);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public void Without_loudnessDb_the_perceptual_loudness_is_made_relative_to_the_target()
    {
        var response = ParserFixtures.Load("player.json");
        response.Delete("loudnessDb", AudioConfig);

        var loudness = PlayerParser.ParseLoudnessDb(response, _log);

        // perceptualLoudnessDb -8.08 against loudnessTargetLkfs -7.
        Assert.NotNull(loudness);
        Assert.Equal(-1.08, loudness.Value, 6);
    }

    [Fact]
    public void Perceptual_loudness_alone_is_not_on_the_same_scale_and_is_ignored()
    {
        var response = ParserFixtures.Load("player.json");
        response.Delete("loudnessDb", AudioConfig);
        response.Delete("loudnessTargetLkfs", AudioConfig);

        Assert.Null(PlayerParser.ParseLoudnessDb(response, _log));
        // A playable track without loudness means the response shape changed.
        Assert.Contains(_log.Warnings, w => w.Contains("loudnessDb", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("player_error.json")] // ERROR "Video unavailable"
    [InlineData("player_login_required.json")] // LOGIN_REQUIRED "Sign in to confirm your age"
    public void Unplayable_tracks_have_no_loudness_and_that_is_not_a_warning(string fixture)
    {
        Assert.Null(PlayerParser.ParseLoudnessDb(ParserFixtures.Load(fixture), _log));
        Assert.Empty(_log.Warnings);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Debug);
    }

    [Theory]
    [InlineData("""{"playerConfig":{"audioConfig":{"loudnessDb":"2.5"}}}""", 2.5)]
    [InlineData("""{"playerConfig":{"audioConfig":{"loudnessDb":0}}}""", 0.0)]
    [InlineData("""{"playerConfig":{"audioConfig":{"loudnessDb":3}}}""", 3.0)]
    public void Loudness_accepts_numbers_and_numeric_strings(string json, double expected)
    {
        Assert.Equal(expected, PlayerParser.ParseLoudnessDb(JsonNode.Parse(json)!, _log));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"playerConfig":{"audioConfig":{"loudnessDb":"loud"}}}""")]
    [InlineData("""{"playerConfig":{"audioConfig":{"loudnessDb":"NaN"}}}""")]
    [InlineData("""{"playerConfig":{"audioConfig":{"loudnessDb":"-Infinity"}}}""")]
    [InlineData("""{"playerConfig":{"audioConfig":{"loudnessDb":null}}}""")]
    [InlineData("""{"playerConfig":{"audioConfig":[]}}""")]
    public void Malformed_or_missing_loudness_is_null(string json)
    {
        Assert.Null(PlayerParser.ParseLoudnessDb(JsonNode.Parse(json)!, _log));
    }
}
