using HushMusic.Core.Models;

namespace HushMusic.Core.Features;

/// <summary>What "Save songs to a playlist" saves from a station, and what the playlist is called.</summary>
public static class RadioAutoSaveRules
{
    /// <summary>A song is saved once it has played this long with its station playing: zapping and jingles save nothing.</summary>
    public static readonly TimeSpan MinimumAirtime = TimeSpan.FromSeconds(30);

    /// <summary>How many of the matcher's best results are checked for a confident match.</summary>
    public const int MatchCandidates = 3;

    // YouTube Music cuts playlist titles at 150 characters.
    private const int MaxTitleLength = 150;

    // Breaks and announcements some stations put in the title field instead of a song (compared without case and spaces).
    private static readonly HashSet<string> Announcements = new(StringComparer.Ordinal)
    {
        "ad", "ads", "adbreak", "advert", "advertisement", "advertising", "commercial", "commercials", "commercialbreak",
        "sponsor", "sponsored", "promo", "jingle", "stationid", "news", "newsupdate", "weather", "traffic", "werbung",
        "nachrichten", "publicite", "publicidad", "pubblicita", "reclama", "publicitate", "stiri", "break",
    };

    /// <summary>
    /// The song's identity within a session: the cleaned "Artist Title" in comparable form, so "Title (Radio Edit)" and
    /// "Title" are the same song. Empty when nothing is left to search for.
    /// </summary>
    public static string SongKey(RadioNowPlaying song, string? stationName)
    {
        ArgumentNullException.ThrowIfNull(song);
        return RadioSongQuery.Comparable(RadioSongQuery.Build(song.Artist, song.Title, stationName));
    }

    /// <summary>
    /// True for an ad break, a news or traffic slot or a jingle announced in place of a song: "Werbung", "Advert - Spot 3".
    /// A song that happens to be called "Break" still counts when an artist is named.
    /// </summary>
    public static bool IsAnnouncement(RadioNowPlaying song)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (song.Artist is { } artist && Announcements.Contains(RadioSongQuery.Comparable(artist)))
        {
            return true;
        }

        var hasArtist = !string.IsNullOrWhiteSpace(song.Artist) && !RadioSongQuery.IsPlaceholderArtist(song.Artist);
        return !hasArtist && Announcements.Contains(RadioSongQuery.Comparable(song.Title));
    }

    /// <summary>The first of <paramref name="candidates"/> that is confidently the announced song, or null.</summary>
    public static Track? PickMatch(RadioNowPlaying song, IReadOnlyList<Track> candidates, string? stationName)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.Take(MatchCandidates).FirstOrDefault(track =>
            !string.IsNullOrEmpty(track.VideoId) && !track.IsLiveRadio && RadioTrackMatcher.IsConfident(song, track, stationName));
    }

    /// <summary>"Heard on Radio X", without the characters YouTube Music refuses in titles.</summary>
    public static string PlaylistTitle(string stationName)
    {
        ArgumentNullException.ThrowIfNull(stationName);
        var name = string.Join(' ', stationName.Replace('<', ' ').Replace('>', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var title = "Heard on " + (name.Length == 0 ? "the radio" : name);
        return title.Length <= MaxTitleLength ? title : title[..MaxTitleLength].TrimEnd();
    }

    public static string PlaylistDescription(string stationName)
    {
        ArgumentNullException.ThrowIfNull(stationName);
        return $"Songs heard on {stationName.Trim()}, added as they played.";
    }
}

/// <summary>
/// What this session did with each song heard on a station, so a song is matched and saved at most once per station and
/// a failure is retried at most once. Not thread-safe; the caller serializes access.
/// </summary>
public sealed class RadioAutoSaveLedger
{
    private readonly Dictionary<(string Station, string Key), (Attempt State, int Failures)> _songs = [];
    private readonly HashSet<(string Station, string VideoId)> _videos = [];
    private readonly HashSet<(string Station, string StreamTitle)> _saved = [];

    private enum Attempt
    {
        InProgress,
        WaitingForRetry,
        Done,
    }

    /// <summary>Starts the first attempt at a song; false when it was tried before (or is being tried).</summary>
    public bool TryBegin(string stationId, string songKey) => _songs.TryAdd((stationId, songKey), (Attempt.InProgress, 0));

    /// <summary>Starts the one retry of a song whose first attempt failed; false otherwise.</summary>
    public bool TryRetry(string stationId, string songKey)
    {
        if (!_songs.TryGetValue((stationId, songKey), out var song) || song.State != Attempt.WaitingForRetry)
        {
            return false;
        }

        _songs[(stationId, songKey)] = (Attempt.InProgress, song.Failures);
        return true;
    }

    /// <summary>The attempt failed. True when the song may be retried (the first failure), false when it is given up.</summary>
    public bool Fail(string stationId, string songKey)
    {
        var failures = _songs.TryGetValue((stationId, songKey), out var song) ? song.Failures + 1 : 1;
        var retry = failures == 1;
        _songs[(stationId, songKey)] = (retry ? Attempt.WaitingForRetry : Attempt.Done, failures);
        return retry;
    }

    /// <summary>Nothing to save (no confident match, or no longer wanted): the song isn't looked up again this session.</summary>
    public void Skip(string stationId, string songKey) => _songs[(stationId, songKey)] = (Attempt.Done, 0);

    /// <summary>The song is in the station's playlist now.</summary>
    public void Saved(string stationId, string songKey, string videoId, string streamTitle)
    {
        _songs[(stationId, songKey)] = (Attempt.Done, 0);
        _videos.Add((stationId, videoId));
        _saved.Add((stationId, streamTitle));
    }

    /// <summary>True when this video was already saved from the station (the same song announced another way).</summary>
    public bool HasVideo(string stationId, string videoId) => _videos.Contains((stationId, videoId));

    public bool WasSaved(string stationId, string streamTitle) => _saved.Contains((stationId, streamTitle));
}
