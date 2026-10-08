namespace HushMusic.InnerTube.Http;

/// <summary>Tunables for the InnerTube HTTP client. Defaults mirror ytmusicapi.</summary>
public sealed class InnerTubeOptions
{
    /// <summary>
    /// Public web-client key that ytmusicapi appends as <c>&amp;key=</c> in cookie mode
    /// (<c>constants.YTM_PARAMS_KEY</c>). It is embedded in the music.youtube.com page; not a secret.
    /// </summary>
    public string ApiKey { get; set; } = "AIzaSyC9XL3ZjWddXya6X74dJoCTL-WEYFDNX30";

    /// <summary>Per-request timeout (ytmusicapi uses 30 s).</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Delay before the single retry of a transient failure (5xx or network error).</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}
