namespace HushMusic.App.ViewModels.YearInReview;

/// <summary>One slide of "Your year in Hush", ready to show. Each kind has its own template in the view.</summary>
public abstract class RecapSlide
{
    /// <summary>Short name: the slide's place in the story, the saved image's file name ("Top song").</summary>
    public required string Name { get; init; }

    /// <summary>The small line above the slide's headline ("YOUR TOP SONG").</summary>
    public required string Eyebrow { get; init; }

    /// <summary>What the slide says, for screen readers.</summary>
    public required string Description { get; init; }

    /// <summary>Art blurred behind the slide; null shows the accent wash instead.</summary>
    public string? BackdropUrl { get; init; }
}

/// <summary>The year and the total listening time.</summary>
public sealed class IntroSlide : RecapSlide
{
    public required string YearText { get; init; }

    public required long Minutes { get; init; }

    public required string Caption { get; init; }
}

/// <summary>The most played song, or the most played album.</summary>
public sealed class FeatureSlide : RecapSlide
{
    public required string Title { get; init; }

    public required string Subtitle { get; init; }

    public required string Detail { get; init; }

    public string? CoverUrl { get; init; }

    /// <summary>The placeholder glyph while the cover loads (or when there is none).</summary>
    public required string Glyph { get; init; }
}

/// <summary>The most played artist, with a round picture.</summary>
public sealed class ArtistSlide : RecapSlide
{
    public required string ArtistName { get; init; }

    public required string Detail { get; init; }

    /// <summary>"That's 12 % of all the music you played."</summary>
    public required string Caption { get; init; }

    public string? PictureUrl { get; init; }
}

/// <summary>A top five (songs or artists).</summary>
public sealed class RankingSlide : RecapSlide
{
    public required string Heading { get; init; }

    public required IReadOnlyList<RecapRankItem> Items { get; init; }
}

public sealed class RecapRankItem(int rank, string title, string subtitle, string detail, string? artUrl, bool isRound)
{
    public string Rank { get; } = rank.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Rows come in one after another, after the slide's heading.</summary>
    public int Order { get; } = rank + 1;

    public string Title { get; } = title;

    public string Subtitle { get; } = subtitle;

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);

    /// <summary>"84 plays".</summary>
    public string Detail { get; } = detail;

    public string? ArtUrl { get; } = artUrl;

    /// <summary>Artists get round pictures, songs square covers.</summary>
    public bool IsRound { get; } = isRound;

    public CornerRadius ArtCorners => IsRound ? new CornerRadius(26) : new CornerRadius(6);
}

/// <summary>When the listening happens: the hours of the day as a clock, and the days of the week.</summary>
public sealed class ClockSlide : RecapSlide
{
    /// <summary>"Night owl".</summary>
    public required string Persona { get; init; }

    public required string Caption { get; init; }

    /// <summary>Listening per hour from midnight, relative to the busiest hour (0 to 1).</summary>
    public required IReadOnlyList<double> Hours { get; init; }

    public required int PeakHour { get; init; }

    /// <summary>"10 PM".</summary>
    public required string PeakLabel { get; init; }

    public required string WeekdayCaption { get; init; }

    public required IReadOnlyList<RecapBar> Weekdays { get; init; }
}

/// <summary>A bar of a small chart on a slide.</summary>
public sealed class RecapBar(string label, double ratio, bool isPeak)
{
    public const double MaxHeight = 52;

    public string Label { get; } = label;

    public double Height { get; } = 4 + (Math.Clamp(ratio, 0, 1) * (MaxHeight - 4));

    public bool IsPeak { get; } = isPeak;

    public double Opacity => IsPeak ? 1 : 0.32;
}

/// <summary>The days with music: how many, a grid of the year, the longest streak and the biggest day.</summary>
public sealed class DaysSlide : RecapSlide
{
    public required string DaysText { get; init; }

    public required string Caption { get; init; }

    /// <summary>January 1st of the year.</summary>
    public required DateOnly FirstDay { get; init; }

    /// <summary>Days after this one haven't happened yet.</summary>
    public required DateOnly LastDay { get; init; }

    /// <summary>Listening on each day of the year relative to the biggest day (0 to 1).</summary>
    public required IReadOnlyList<double> Days { get; init; }

    public required string StreakText { get; init; }

    public required string BiggestDayText { get; init; }
}

/// <summary>Live radio: the top station and the hours.</summary>
public sealed class RadioSlide : RecapSlide
{
    public required string StationName { get; init; }

    public string? LogoUrl { get; init; }

    public required string Detail { get; init; }

    public required string Caption { get; init; }
}

/// <summary>The summary card at the end, made to be saved and shared.</summary>
public sealed class OutroSlide : RecapSlide
{
    public required string Heading { get; init; }

    public string? CoverUrl { get; init; }

    public required IReadOnlyList<RecapStat> Stats { get; init; }

    public required IReadOnlyList<RecapRankItem> TopArtists { get; init; }

    public required IReadOnlyList<RecapRankItem> TopSongs { get; init; }
}

/// <summary>A number with its label on the summary card ("12,480" / "Minutes").</summary>
public sealed record RecapStat(string Value, string Label);
