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

/// <summary>No playable stream could be resolved for a track.</summary>
public sealed class StreamResolutionException(string videoId, string message, Exception? innerException = null)
    : HushException(message, innerException)
{
    public string VideoId { get; } = videoId;
}
