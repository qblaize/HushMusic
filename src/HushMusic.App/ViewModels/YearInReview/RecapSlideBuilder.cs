using System.Globalization;
using HushMusic.App.ViewModels.Pages;
using HushMusic.Core.Features.Stats;

namespace HushMusic.App.ViewModels.YearInReview;

/// <summary>Turns a <see cref="YearReview"/> into the slides of the story, skipping the ones the year has nothing for.</summary>
public static class RecapSlideBuilder
{
    /// <summary>Covers shown large are requested at this size (sharp up to about twice their size on screen).</summary>
    public const int CoverPixels = 720;

    public const int RowArtPixels = 226;

    private const string SongGlyph = "";
    private const string AlbumGlyph = "";

    // An album is about this long, for the intro's "enough to play N albums".
    private static readonly TimeSpan AlbumLength = TimeSpan.FromMinutes(45);

    public static IReadOnlyList<RecapSlide> Build(YearReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        var culture = CultureInfo.CurrentCulture;
        var year = review.Year.ToString(CultureInfo.InvariantCulture);
        var topSong = review.TopSongs.FirstOrDefault();
        var cover = topSong?.ArtUrl;
        List<RecapSlide> slides = [];

        var minutes = (long)Math.Round(review.TotalListened.TotalMinutes, MidpointRounding.AwayFromZero);
        var funFact = FunFact(review.TotalListened);
        slides.Add(new IntroSlide
        {
            Name = "Intro",
            Eyebrow = review.IsInProgress ? "Your year so far" : "Your year in Hush",
            Description = $"Your {year} in Hush. You listened for {minutes.ToString("N0", culture)} minutes. {funFact}",
            BackdropUrl = cover,
            YearText = year,
            Minutes = minutes,
            Caption = funFact,
        });

        if (topSong is not null)
        {
            var detail = Detail(topSong.Plays, topSong.Listened);
            slides.Add(new FeatureSlide
            {
                Name = "Top song",
                Eyebrow = "Your top song",
                Description = $"Your top song: {topSong.Title} by {topSong.Artists}. {detail}.",
                BackdropUrl = cover,
                Title = topSong.Title,
                Subtitle = topSong.Artists,
                Detail = detail,
                CoverUrl = RecapArt.Sized(cover, CoverPixels),
                Glyph = SongGlyph,
            });
        }

        if (review.TopSongs.Count >= 3)
        {
            var items = review.TopSongs.Select((s, i) => new RecapRankItem(i + 1, s.Title, s.Artists, StatsViewModel.FormatPlays(s.Plays), RecapArt.Sized(s.ArtUrl, RowArtPixels), isRound: false)).ToList();
            slides.Add(new RankingSlide
            {
                Name = "Top songs",
                Eyebrow = "Your top songs",
                Description = "Your top songs: " + string.Join(", ", items.Select(i => $"{i.Rank}. {i.Title} by {i.Subtitle}")),
                BackdropUrl = cover,
                Heading = review.IsInProgress ? "On repeat so far" : "On repeat all year",
                Items = items,
            });
        }

        if (review.TopArtists.FirstOrDefault() is { } artist)
        {
            var detail = Detail(artist.Plays, artist.Listened);
            var share = review.MusicListened > TimeSpan.Zero ? (int)Math.Round(artist.Listened / review.MusicListened * 100) : 0;
            var caption = share >= 1 ? $"That's {share}% of all the music you played." : $"Your most played artist of {year}.";
            slides.Add(new ArtistSlide
            {
                Name = "Top artist",
                Eyebrow = "Your top artist",
                Description = $"Your top artist: {artist.Name}. {detail}. {caption}",
                BackdropUrl = artist.ArtUrl ?? cover,
                ArtistName = artist.Name,
                Detail = detail,
                Caption = caption,
                PictureUrl = RecapArt.Sized(artist.ArtUrl ?? cover, CoverPixels),
            });
        }

        if (review.TopArtists.Count >= 3)
        {
            var items = review.TopArtists.Select((a, i) => new RecapRankItem(i + 1, a.Name, string.Empty, StatsViewModel.FormatPlays(a.Plays), RecapArt.Sized(a.ArtUrl, RowArtPixels), isRound: true)).ToList();
            slides.Add(new RankingSlide
            {
                Name = "Top artists",
                Eyebrow = "Your top artists",
                Description = "Your top artists: " + string.Join(", ", items.Select(i => $"{i.Rank}. {i.Title}")),
                BackdropUrl = review.TopArtists[0].ArtUrl ?? cover,
                Heading = "The ones you kept coming back to",
                Items = items,
            });
        }

        // The top song's own single would only repeat the slide before: the first real album instead.
        if (review.TopAlbums.FirstOrDefault(a => !IsSingleOf(a, topSong)) is { } album)
        {
            var detail = Detail(album.Plays, album.Listened);
            slides.Add(new FeatureSlide
            {
                Name = "Top album",
                Eyebrow = "Your top album",
                Description = $"Your top album: {album.Title} by {album.Artists}. {detail}.",
                BackdropUrl = album.ArtUrl ?? cover,
                Title = album.Title,
                Subtitle = album.Artists,
                Detail = detail,
                CoverUrl = RecapArt.Sized(album.ArtUrl, CoverPixels),
                Glyph = AlbumGlyph,
            });
        }

        if (review.PeakHour is { } peakHour)
        {
            slides.Add(Clock(review, peakHour, culture));
        }

        if (review.DaysListened > 0)
        {
            slides.Add(Days(review, year, culture));
        }

        if (review.RadioListened >= TimeSpan.FromMinutes(1) && review.TopStations.FirstOrDefault() is { } station)
        {
            var caption = $"{StatsViewModel.FormatListened(review.RadioListened)} of live radio, across {Count(review.Stations, "station")}.";
            slides.Add(new RadioSlide
            {
                Name = "Radio",
                Eyebrow = "On the radio",
                Description = $"Your top radio station: {station.Name}. {caption}",
                StationName = station.Name,
                LogoUrl = station.ArtUrl,
                Detail = $"Your top station · {StatsViewModel.FormatListened(station.Listened)}",
                Caption = caption,
            });
        }

        var minutesText = minutes.ToString("N0", culture);
        slides.Add(new OutroSlide
        {
            Name = "Summary",
            Eyebrow = review.IsInProgress ? "Your year so far" : "Your year in Hush",
            Description = $"Your {year} in Hush: {minutesText} minutes, {review.DaysListened} days with music, {review.DistinctSongs} different songs.",
            BackdropUrl = cover,
            Heading = $"Your {year} in Hush",
            CoverUrl = RecapArt.Sized(cover, CoverPixels),
            Stats =
            [
                new RecapStat(minutesText, "Minutes"),
                new RecapStat(review.DaysListened.ToString("N0", culture), review.DaysListened == 1 ? "Day" : "Days"),
                new RecapStat(review.DistinctSongs.ToString("N0", culture), review.DistinctSongs == 1 ? "Song" : "Songs"),
            ],
            TopArtists = [.. review.TopArtists.Select((a, i) => new RecapRankItem(i + 1, a.Name, string.Empty, string.Empty, null, isRound: true))],
            TopSongs = [.. review.TopSongs.Select((s, i) => new RecapRankItem(i + 1, s.Title, s.Artists, string.Empty, null, isRound: false))],
        });

        return slides;
    }

    /// <summary>"10 PM", or "22:00" where the clock is 24-hour.</summary>
    public static string HourLabel(int hour, CultureInfo culture)
    {
        var format = culture.DateTimeFormat;
        if (!format.ShortTimePattern.Contains('t', StringComparison.Ordinal))
        {
            return hour.ToString("00", culture) + ":00";
        }

        var designator = hour < 12 ? format.AMDesignator : format.PMDesignator;
        var twelve = hour % 12 == 0 ? 12 : hour % 12;
        return string.IsNullOrEmpty(designator) ? twelve.ToString(culture) : $"{twelve} {designator}";
    }

    private static ClockSlide Clock(YearReview review, int peakHour, CultureInfo culture)
    {
        var max = review.ByHour.Max();
        var hours = review.ByHour.Select(h => max > TimeSpan.Zero ? h / max : 0).ToList();
        var peakLabel = HourLabel(peakHour, culture);

        // The core counts weekdays from Monday; the bars start the week where the user's region does.
        var format = culture.DateTimeFormat;
        var maxDay = review.ByWeekday.Max();
        List<RecapBar> weekdays = [];
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)format.FirstDayOfWeek + i) % 7);
            var time = review.ByWeekday[((int)day + 6) % 7];
            weekdays.Add(new RecapBar(format.GetShortestDayName(day), maxDay > TimeSpan.Zero ? time / maxDay : 0, day == review.PeakWeekday));
        }

        var persona = peakHour switch
        {
            >= 5 and < 9 => "Early bird",
            >= 9 and < 12 => "Morning person",
            >= 12 and < 17 => "Afternoon listener",
            >= 17 and < 22 => "Evening listener",
            _ => "Night owl",
        };
        var caption = $"Your music peaks around {peakLabel}.";
        var weekdayCaption = review.PeakWeekday is { } peakDay ? $"You listen most on {format.GetDayName(peakDay)}s." : string.Empty;
        return new ClockSlide
        {
            Name = "When you listen",
            Eyebrow = "When you listen",
            Description = $"{persona}. {caption} {weekdayCaption}",
            Persona = persona,
            Caption = caption,
            Hours = hours,
            PeakHour = peakHour,
            PeakLabel = peakLabel,
            WeekdayCaption = weekdayCaption,
            Weekdays = weekdays,
        };
    }

    private static DaysSlide Days(YearReview review, string year, CultureInfo culture)
    {
        var max = review.ByDay.Count == 0 ? TimeSpan.Zero : review.ByDay.Max();
        var caption = review.IsInProgress
            ? $"days with music, out of {review.DaysCovered.ToString("N0", culture)} so far"
            : $"days with music in {year}";
        var streak = review.LongestStreak is { Days: > 1 } run
            ? $"Longest streak: {run.Days} days in a row, {run.First.ToString("MMM d", culture)} – {run.Last.ToString("MMM d", culture)}"
            : "Longest streak: 1 day";
        var biggest = review.BiggestDay is { } day
            ? $"Biggest day: {day.ToString("dddd, MMMM d", culture)} · {StatsViewModel.FormatListened(review.BiggestDayListened)}"
            : string.Empty;
        return new DaysSlide
        {
            Name = "Your days",
            Eyebrow = "Your days",
            Description = $"{review.DaysListened} {caption}. {streak}. {biggest}",
            DaysText = review.DaysListened.ToString("N0", culture),
            Caption = caption,
            FirstDay = review.FirstDay,
            LastDay = review.LastDay,
            Days = [.. review.ByDay.Select(d => max > TimeSpan.Zero ? d / max : 0)],
            StreakText = streak,
            BiggestDayText = biggest,
        };
    }

    private static bool IsSingleOf(TopAlbum album, TopSong? song) =>
        song is not null
        && string.Equals(album.Title, song.Title, StringComparison.CurrentCultureIgnoreCase)
        && (album.BrowseId is null || string.Equals(album.BrowseId, song.AlbumId, StringComparison.Ordinal));

    private static string Detail(int plays, TimeSpan listened) => $"{StatsViewModel.FormatPlays(plays)} · {StatsViewModel.FormatListened(listened)}";

    private static string FunFact(TimeSpan total)
    {
        if (total >= TimeSpan.FromDays(2))
        {
            var days = (int)total.TotalDays;
            return total.Hours == 0
                ? $"That's {days} whole days of music, back to back."
                : $"That's {days} days and {Count(total.Hours, "hour")} of music, back to back.";
        }

        if (total >= TimeSpan.FromDays(1))
        {
            return "That's more than a whole day of music, back to back.";
        }

        var albums = (int)(total / AlbumLength);
        return albums >= 2 ? $"Enough to play {albums} albums from start to finish." : "A quiet start. Here's what stood out.";
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count.ToString("N0", CultureInfo.CurrentCulture)} {noun}s";
}
