using HushMusic.Core.Models;

namespace HushMusic.Core.Features.NewReleases;

/// <summary>
/// The decisions of the new-release check, as pure functions of <see cref="NewReleaseState"/> and the time: when to read
/// the followed artists, which artist to check next, what counts as new, and when to announce.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The followed artists are read once every <see cref="Interval"/>; each artist's page is checked at most once in
/// that time, and at most <see cref="MaxChecksPerDay"/> pages a day (the least recently checked first), so a long list is
/// covered over several days.</item>
/// <item>An artist's first check (and one after <see cref="BaselineExpiry"/> without a check) only records what is already
/// out. A page that lists no releases records nothing, so a page that failed to load can't become the baseline.</item>
/// <item>A release is new when neither its browse id nor its type and title were seen before (YouTube Music sometimes gives
/// a release a new id), and it is from this year or last year (an old release that reappears isn't news).</item>
/// </list>
/// </remarks>
public static class NewReleasePlanner
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    /// <summary>A check this long after the previous one starts over silently: what changed meanwhile is too old to announce.</summary>
    public static readonly TimeSpan BaselineExpiry = TimeSpan.FromDays(60);

    /// <summary>Releases found but not announced for this long are dropped (the feature was off, or Hush wasn't running).</summary>
    public static readonly TimeSpan PendingExpiry = TimeSpan.FromDays(7);

    public const int MaxChecksPerDay = 100;

    /// <summary>Release keys kept per artist; the oldest go first.</summary>
    public const int MaxKnownPerArtist = 400;

    /// <summary>What to do next.</summary>
    public abstract record Step;

    /// <summary>Read the list of followed artists again.</summary>
    public sealed record RefreshFollowed : Step;

    /// <summary>Check this artist's page.</summary>
    public sealed record CheckArtist(string ArtistId) : Step;

    /// <summary>Nothing to do until then: announce what was found, then wait.</summary>
    public sealed record WaitUntil(DateTimeOffset Until) : Step;

    public static Step Next(NewReleaseState state, DateTimeOffset now, int maxChecksPerDay = MaxChecksPerDay)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.FollowedRefreshedAt is not { } refreshed || IsDue(refreshed, now))
        {
            return new RefreshFollowed();
        }

        var nextRefresh = refreshed + Interval;
        var due = state.Artists
            .Where(a => a.CheckedAt is not { } checkedAt || IsDue(checkedAt, now))
            .OrderBy(a => a.CheckedAt ?? DateTimeOffset.MinValue)
            .ThenBy(a => a.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        if (due is not null)
        {
            if (ChecksToday(state, now) < maxChecksPerDay)
            {
                return new CheckArtist(due.Id);
            }

            return new WaitUntil(Earliest((state.WindowStart ?? now) + Interval, nextRefresh));
        }

        var nextCheck = state.Artists.Count == 0 ? nextRefresh : state.Artists.Min(a => a.CheckedAt!.Value) + Interval;
        return new WaitUntil(Earliest(nextCheck, nextRefresh));
    }

    /// <summary>
    /// Replaces the followed artists with <paramref name="followed"/>, keeping what is known about those already followed;
    /// artists no longer followed are forgotten.
    /// </summary>
    public static void SetFollowed(NewReleaseState state, IReadOnlyList<Artist> followed, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(followed);
        var known = state.Artists.ToDictionary(a => a.Id, StringComparer.Ordinal);
        var artists = new List<FollowedArtist>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artist in followed)
        {
            var id = ChannelId(artist.BrowseId);
            if (id.Length == 0 || !seen.Add(id))
            {
                continue;
            }

            if (known.TryGetValue(id, out var existing))
            {
                existing.Name = string.IsNullOrWhiteSpace(artist.Title) ? existing.Name : artist.Title;
                artists.Add(existing);
            }
            else
            {
                artists.Add(new FollowedArtist { Id = id, Name = artist.Title });
            }
        }

        state.Artists = artists;
        state.FollowedRefreshedAt = now;
    }

    /// <summary>
    /// Records a check of <paramref name="artistId"/>'s page and returns its new releases (also added to
    /// <see cref="NewReleaseState.Pending"/>). Empty for a first check.
    /// </summary>
    public static IReadOnlyList<PendingRelease> RecordCheck(NewReleaseState state, string artistId, ArtistPage page, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(page);
        if (state.Artists.Find(a => a.Id == artistId) is not { } artist)
        {
            return [];
        }

        CountCheck(state, now);
        var previous = artist.CheckedAt;
        artist.CheckedAt = now;
        if (!string.IsNullOrWhiteSpace(page.Artist.Title))
        {
            artist.Name = page.Artist.Title;
        }

        var releases = Releases(page);
        if (releases.Count == 0)
        {
            return [];
        }

        var baseline = artist.Known is null || previous is not { } last || now - last > BaselineExpiry;
        var found = new List<PendingRelease>();
        if (!baseline)
        {
            var known = artist.Known!.ToHashSet(StringComparer.Ordinal);
            foreach (var album in releases)
            {
                if (!known.Contains(album.BrowseId) && !known.Contains(TitleKey(album)) && IsRecent(album, now)
                    && !state.Pending.Exists(p => p.BrowseId == album.BrowseId))
                {
                    found.Add(new PendingRelease(artist.Id, artist.Name, album.BrowseId, album.Title, album.Type, album.Year, album.ThumbnailFor(226), now));
                }
            }
        }

        artist.Known = [.. releases.SelectMany(a => new[] { a.BrowseId, TitleKey(a) }).Concat(artist.Known ?? []).Distinct(StringComparer.Ordinal).Take(MaxKnownPerArtist)];
        state.Pending.AddRange(found);
        return found;
    }

    /// <summary>A check that failed for this artist only: it is skipped until its next turn, and counts towards the day's checks.</summary>
    public static void RecordFailedCheck(NewReleaseState state, string artistId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Artists.Find(a => a.Id == artistId) is { } artist)
        {
            CountCheck(state, now);
            artist.CheckedAt = now;
        }
    }

    /// <summary>Takes the releases waiting to be announced, newest first, leaving out those found too long ago.</summary>
    public static IReadOnlyList<PendingRelease> TakePending(NewReleaseState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        var fresh = state.Pending
            .Where(p => now - p.FoundAt <= PendingExpiry)
            .OrderByDescending(p => int.TryParse(p.Year, out var year) ? year : int.MaxValue)
            .ThenByDescending(p => p.FoundAt)
            .ToList();
        state.Pending.Clear();
        return fresh;
    }

    /// <summary>The albums, singles and EPs on an artist's page, in page order.</summary>
    public static IReadOnlyList<Album> Releases(ArtistPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return [.. page.Sections
            .SelectMany(s => s.Items)
            .OfType<Album>()
            .Where(a => a.BrowseId.StartsWith("MPRE", StringComparison.Ordinal))
            .DistinctBy(a => a.BrowseId, StringComparer.Ordinal)];
    }

    /// <summary>From this year or last year, or of an unknown year.</summary>
    public static bool IsRecent(Album album, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(album);
        return !int.TryParse(album.Year, out var year) || year >= now.Year - 1;
    }

    private static string TitleKey(Album album) =>
        $"title:{album.Type}:{album.Title.Trim().ToUpperInvariant()}";

    // Library rows can link artists with the "MPLA" prefix of their library page.
    private static string ChannelId(string browseId) =>
        browseId.StartsWith("MPLA", StringComparison.Ordinal) ? browseId[4..] : browseId;

    // Also due when the clock went back past the last time.
    private static bool IsDue(DateTimeOffset last, DateTimeOffset now) => now - last >= Interval || last > now;

    private static int ChecksToday(NewReleaseState state, DateTimeOffset now) =>
        state.WindowStart is { } start && !IsDue(start, now) ? state.ChecksInWindow : 0;

    private static void CountCheck(NewReleaseState state, DateTimeOffset now)
    {
        if (state.WindowStart is not { } start || IsDue(start, now))
        {
            state.WindowStart = now;
            state.ChecksInWindow = 0;
        }

        state.ChecksInWindow++;
    }

    private static DateTimeOffset Earliest(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;
}
