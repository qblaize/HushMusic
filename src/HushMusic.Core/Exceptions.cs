namespace HushMusic.Core;

public class HushException : Exception
{
    public HushException(string message)
        : base(message)
    {
    }

    public HushException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The operation needs a signed-in account.</summary>
public sealed class AuthRequiredException(string message = "Sign in to YouTube Music to use this feature.")
    : HushException(message);

/// <summary>An InnerTube request failed or returned something we could not understand.</summary>
public sealed class InnerTubeException(string endpoint, string message, int? statusCode = null, Exception? innerException = null)
    : HushException(message, innerException)
{
    public string Endpoint { get; } = endpoint;

    public int? StatusCode { get; } = statusCode;
}

/// <summary>
/// YouTube Music refused to add songs to a playlist because at least one of them is already in it. Nothing was added;
/// adding them again with duplicates allowed puts them in anyway.
/// </summary>
public sealed class AlreadyInPlaylistException(string playlistId, IReadOnlyList<string> videoIds, Exception? innerException = null)
    : HushException(
        videoIds.Count == 1 ? "This song is already in the playlist." : "Some of these songs are already in the playlist.",
        innerException)
{
    public string PlaylistId { get; } = playlistId;

    /// <summary>Every song of the refused request; YouTube Music doesn't say which ones are duplicates.</summary>
    public IReadOnlyList<string> VideoIds { get; } = videoIds;
}

/// <summary>No playable stream could be resolved for a track.</summary>
public sealed class StreamResolutionException(string videoId, string message, Exception? innerException = null)
    : HushException(message, innerException)
{
    public string VideoId { get; } = videoId;
}
