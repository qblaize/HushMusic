using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features.Stats;
using HushMusic.Core.Models;
using HushMusic.Core.Radio;
using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class StationOfTheDayPickerTests
{
    private static readonly DateOnly Day = new(2026, 10, 12);

    private static readonly IReadOnlyList<RadioGenre> Genres =
    [
        new RadioGenre("deep-house", "Deep House", ["deep house"], [Station("c1", "Curated Deep", "deep house")]),
        new RadioGenre("lofi", "Lo-fi", ["lofi", "lo-fi"], [Station("c2", "Curated Lofi", "lofi", "chillhop")]),
    ];

    [Theory]
    [InlineData("  Deep   House ", "deep house")]
    [InlineData("Lo-Fi", "lo-fi")]
    [InlineData("drum & bass", "drum & bass")]
    [InlineData("#charts", null)]
    [InlineData("2000s", null)]
    [InlineData("radio", null)]
    [InlineData("internet radio", null)]
    [InlineData("pop", "pop")]
    [InlineData("hd", null)]
    [InlineData(null, null)]
    public void Keeps_only_tags_that_name_a_kind_of_music(string? tag, string? expected) =>
        Assert.Equal(expected, StationOfTheDayPicker.NormalizeTag(tag));

    [Fact]
    public void Adds_up_listening_per_station()
    {
        PlayRecord[] plays =
        [
            Play("a", "A FM", PlayKind.Radio, 600),
            Play("song", "Song", PlayKind.Track, 3000),
            Play("a", "A FM", PlayKind.Radio, 1200),
            Play("b", "B FM", PlayKind.Radio, 4000),
        ];

        var listened = StationOfTheDayPicker.Listening(plays);

        Assert.Equal(["b", "a"], listened.Select(l => l.StationId));
        Assert.Equal(TimeSpan.FromSeconds(1800), listened[1].Listened);
    }

    [Fact]
    public void Favourites_and_listening_make_the_taste_with_the_first_tag_counting_most()
    {
        RadioStation[] favorites = [Station("f1", "Fav One", "jazz", "smooth jazz", "radio"), Station("f2", "Fav Two", "jazz")];
        ListenedStation[] listened =
        [
            new("c1", "Curated Deep", TimeSpan.FromHours(20)), // curated: its genre's tags; capped at ten hours
            new("x", "1.FM - Deep Techno & Lofi Beats", TimeSpan.FromHours(1)), // unknown: genres named in it
        ];

        var taste = StationOfTheDayPicker.Taste(favorites, listened, Genres);

        Assert.Equal(["deep house", "jazz", "smooth jazz", "lofi"], taste.Select(t => t.Tag));
        Assert.Equal(10, taste[0].Weight);
        Assert.Equal(6, taste[1].Weight);
        Assert.Equal(1.5, taste[2].Weight);
        Assert.Equal(1, taste[3].Weight);
    }

    [Fact]
    public void A_few_minutes_of_listening_say_nothing_about_taste()
    {
        var taste = StationOfTheDayPicker.Taste([], [new ListenedStation("c1", "Curated Deep", TimeSpan.FromMinutes(4))], Genres);

        Assert.Empty(taste);
    }

    [Fact]
    public void The_genre_stays_the_same_all_day_and_varies_between_days()
    {
        GenreWeight[] taste = [new("jazz", 3), new("lofi", 3)];

        var today = StationOfTheDayPicker.ChooseGenre(taste, Genres, Day);
        Assert.Equal(today, StationOfTheDayPicker.ChooseGenre(taste, Genres, Day));
        Assert.True(today?.IsFromTaste);

        var month = Enumerable.Range(0, 30).Select(d => StationOfTheDayPicker.ChooseGenre(taste, Genres, Day.AddDays(d))!.Tag).ToHashSet();
        Assert.Equal(["jazz", "lofi"], month.Order());

        // A curated genre's tag shows its name.
        Assert.Equal("Lo-fi", StationOfTheDayPicker.ChooseGenre([new("lofi", 1)], Genres, Day)?.Label);
        Assert.Equal("Jazz", StationOfTheDayPicker.ChooseGenre([new("jazz", 1)], Genres, Day)?.Label);
    }

    [Fact]
    public void Without_a_taste_the_curated_genres_take_turns()
    {
        var first = StationOfTheDayPicker.ChooseGenre([], Genres, Day);
        var second = StationOfTheDayPicker.ChooseGenre([], Genres, Day.AddDays(1));

        Assert.False(first?.IsFromTaste);
        Assert.NotEqual(first?.Label, second?.Label);
        Assert.Contains(first?.Label, Genres.Select(g => g.Name));
        Assert.Null(StationOfTheDayPicker.ChooseGenre([], [], Day));
    }

    [Fact]
    public void Candidates_are_curated_first_then_the_directory_without_favourites_or_repeats()
    {
        RadioStation[] directory =
        [
            Station("d1", "Dir One", "deep house"),
            Station("fav", "Favourite", "deep house"),
            Station("c1", "Curated Deep", "deep house"),
            Station("d2", "DIR  one", "deep house"),
            Station("d3", "Dir Three", "deep house"),
        ];

        var candidates = StationOfTheDayPicker.Candidates("deep house", directory, Genres, new HashSet<string> { "fav" });

        Assert.Equal(["c1", "d1", "d3"], candidates.Select(s => s.Id));

        var many = Enumerable.Range(0, 50).Select(i => Station("m" + i, "Many " + i, "deep house")).ToList();
        Assert.Equal(StationOfTheDayPicker.CandidatePool, StationOfTheDayPicker.Candidates("deep house", many, Genres, new HashSet<string>()).Count);
    }

    [Fact]
    public void Picks_the_same_station_all_day_preferring_one_not_heard_yet()
    {
        var candidates = Enumerable.Range(0, 10).Select(i => Station("s" + i, "Station " + i, "jazz")).ToList();
        var none = new HashSet<string>();

        var pick = StationOfTheDayPicker.Pick(candidates, Day, 0, none);
        Assert.Equal(pick, StationOfTheDayPicker.Pick(candidates, Day, 0, none));
        Assert.NotEqual(pick?.Id, StationOfTheDayPicker.Pick(candidates, Day, 1, none, skip: pick?.Id)?.Id);

        var heard = candidates.Where(s => s.Id != "s7").Select(s => s.Id).ToHashSet();
        Assert.Equal("s7", StationOfTheDayPicker.Pick(candidates, Day, 0, heard)?.Id);

        // Everything heard: still a pick. Nothing left: none.
        Assert.NotNull(StationOfTheDayPicker.Pick(candidates, Day, 0, candidates.Select(s => s.Id).ToHashSet()));
        Assert.Null(StationOfTheDayPicker.Pick([candidates[0]], Day, 1, none, skip: "s0"));
    }

    internal static RadioStation Station(string id, string name, params string[] tags) => new()
    {
        Id = id,
        Name = name,
        StreamUrl = $"https://stream.example.com/{id}.mp3",
        Tags = tags,
        Country = "Germany",
    };

    internal static PlayRecord Play(string itemId, string title, PlayKind kind, int seconds) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Kind = kind,
        ItemId = itemId,
        Title = title,
        Start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        ListenedSeconds = seconds,
    };
}

public sealed class StationOfTheDayServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hush-sotd-tests-" + Guid.NewGuid().ToString("N"));
    private readonly SotdFakeDirectory _directory = new();
    private readonly SotdFakeFavorites _favorites = new();
    private readonly SotdFakePlayLog _playLog = new();
    private readonly ManualTimeProvider _time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Picks_once_a_day_from_the_favourite_genre_and_keeps_the_pick_across_a_restart()
    {
        _favorites.Items = [StationOfTheDayPickerTests.Station("fav", "My Polka", "polka")];
        _directory.Stations = [.. Enumerable.Range(0, 8).Select(i => StationOfTheDayPickerTests.Station("p" + i, "Polka " + i, "polka")), _favorites.Items[0]];

        var pick = await Service().GetAsync(Ct);

        Assert.NotNull(pick);
        Assert.StartsWith("p", pick.Station.Id, StringComparison.Ordinal);
        Assert.Equal("Polka", pick.Genre);
        Assert.True(pick.IsFromTaste);
        Assert.True(pick.CanReroll);
        Assert.Equal("polka", Assert.Single(Assert.Single(_directory.Calls)));

        var again = await Service().GetAsync(Ct);
        Assert.Equal(pick.Station.Id, again?.Station.Id);
        Assert.Equal(pick.Day, again?.Day);
        Assert.Single(_directory.Calls);

        _time.Advance(TimeSpan.FromDays(1));
        var tomorrow = await Service().GetAsync(Ct);
        Assert.Equal(pick.Day.AddDays(1), tomorrow?.Day);
        Assert.Equal(2, _directory.Calls.Count);
    }

    [Fact]
    public async Task Another_one_picks_a_different_station_once_a_day_without_asking_the_directory()
    {
        _favorites.Items = [StationOfTheDayPickerTests.Station("fav", "My Polka", "polka")];
        _directory.Stations = [.. Enumerable.Range(0, 8).Select(i => StationOfTheDayPickerTests.Station("p" + i, "Polka " + i, "polka"))];
        var service = Service();
        var first = await service.GetAsync(Ct);

        var second = await service.RerollAsync(Ct);
        var third = await service.RerollAsync(Ct);

        Assert.NotEqual(first?.Station.Id, second?.Station.Id);
        Assert.False(second?.CanReroll);
        Assert.Equal(second, third);
        var restarted = await Service().GetAsync(Ct);
        Assert.Equal(second?.Station.Id, restarted?.Station.Id);
        Assert.False(restarted?.CanReroll);
        Assert.Single(_directory.Calls);
    }

    [Fact]
    public async Task Without_history_a_curated_genre_is_used_and_a_directory_failure_still_gives_a_pick()
    {
        _directory.Failure = new HushException("offline");

        var pick = await Service().GetAsync(Ct);

        Assert.NotNull(pick);
        Assert.False(pick.IsFromTaste);
        Assert.Contains(pick.Station.Id, CuratedRadio.Genres.SelectMany(g => g.Stations).Select(s => s.Id));
    }

    [Fact]
    public async Task Prefers_stations_not_listened_to_and_never_picks_a_favourite()
    {
        _favorites.Items = [StationOfTheDayPickerTests.Station("fav", "My Polka", "polka")];
        _directory.Stations = [.. Enumerable.Range(0, 4).Select(i => StationOfTheDayPickerTests.Station("p" + i, "Polka " + i, "polka"))];
        _playLog.Plays = [.. new[] { "p0", "p1", "p3" }.Select(id => StationOfTheDayPickerTests.Play(id, "Polka", PlayKind.Radio, 60))];

        var pick = await Service().GetAsync(Ct);

        Assert.Equal("p2", pick?.Station.Id);
    }

    private StationOfTheDayService Service() =>
        new(_directory, _favorites, _playLog, new AppPaths(_root), _time, NullLogger<StationOfTheDayService>.Instance);
}

internal sealed class SotdFakeDirectory : IRadioDirectory
{
    public IReadOnlyList<RadioStation> Stations { get; set; } = [];

    public Exception? Failure { get; set; }

    public List<IReadOnlyList<string>> Calls { get; } = [];

    public Task<IReadOnlyList<RadioStation>> GetByTagsAsync(IReadOnlyList<string> tags, int limit, CancellationToken cancellationToken = default)
    {
        Calls.Add(tags);
        return Failure is { } failure ? Task.FromException<IReadOnlyList<RadioStation>>(failure) : Task.FromResult<IReadOnlyList<RadioStation>>([.. Stations.Take(limit)]);
    }

    public Task<IReadOnlyList<RadioStation>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task CountClickAsync(string stationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class SotdFakeFavorites : IRadioFavorites
{
    public IReadOnlyList<RadioStation> Items { get; set; } = [];

    public event EventHandler? Changed { add { } remove { } }

    public bool Contains(string stationId) => Items.Any(s => s.Id == stationId);

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetAsync(RadioStation station, bool isFavorite, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class SotdFakePlayLog : IPlayLog
{
    public IReadOnlyList<PlayRecord> Plays { get; set; } = [];

    public event EventHandler? Changed { add { } remove { } }

    public Task AppendAsync(PlayRecord record, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<IReadOnlyList<PlayRecord>> ReadAsync(DateTimeOffset? since = null, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlayRecord>>([.. Plays.Where(p => since is null || p.Start >= since)]);

    public Task ClearAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
