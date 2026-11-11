using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features.NewReleases;

/// <summary>
/// What the new-release check remembers between runs (<c>new-releases.json</c>): the followed artists, the releases seen
/// on each one's page, how far today's round got, and releases found but not announced yet.
/// </summary>
public sealed class NewReleaseState
{
    public int Version { get; set; } = 1;

    /// <summary>When the list of followed artists was last read from the account.</summary>
    public DateTimeOffset? FollowedRefreshedAt { get; set; }

    /// <summary>Start of the current 24-hour window of artist checks, which <see cref="NewReleasePlanner.MaxChecksPerDay"/> caps.</summary>
    public DateTimeOffset? WindowStart { get; set; }

    public int ChecksInWindow { get; set; }

    public List<FollowedArtist> Artists { get; set; } = [];

    /// <summary>Found, not announced yet (announced when the round's checks are done).</summary>
    public List<PendingRelease> Pending { get; set; } = [];
}

/// <summary>An artist the account subscribes to, and what was on their page at the last check.</summary>
public sealed class FollowedArtist
{
    /// <summary>The channel id ("UC…").</summary>
    public required string Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset? CheckedAt { get; set; }

    /// <summary>
    /// Keys of the releases seen on the page (their browse ids, and their type and title), newest first. Null until the
    /// first check, which only records what is there.
    /// </summary>
    public List<string>? Known { get; set; }
}

/// <summary>A release found on a followed artist's page that hasn't been announced yet.</summary>
public sealed record PendingRelease(
    string ArtistId,
    string ArtistName,
    string BrowseId,
    string Title,
    AlbumType Type,
    string? Year,
    Thumbnail? Cover,
    DateTimeOffset FoundAt)
{
    public NewRelease ToRelease() => new(
        ArtistId,
        ArtistName,
        new Album
        {
            Title = Title,
            BrowseId = BrowseId,
            Type = Type,
            Year = Year,
            Thumbnails = Cover is null ? [] : [Cover],
            Artists = [new ArtistRef(ArtistName, ArtistId)],
        });
}
