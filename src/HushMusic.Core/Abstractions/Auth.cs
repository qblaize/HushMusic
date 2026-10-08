namespace HushMusic.Core.Abstractions;

// Sign-in is cookie based: InnerTube rejects OAuth Bearer tokens since 2025-08
// (ytmusicapi issue #813), so the OAuth device flow from the original brief is not used.

public enum AuthStatus
{
    SignedOut,
    SigningIn,
    SignedIn,

    /// <summary>Stored cookies were rejected by YouTube Music; the user must sign in again.</summary>
    Expired,
}

public enum AuthMode
{
    None,
    Cookies,
}

/// <summary>A browser cookie captured by the WebView2 sign-in window.</summary>
public sealed record BrowserCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    DateTimeOffset? Expires,
    bool IsSecure,
    bool IsHttpOnly);

public sealed class AuthStatusChangedEventArgs(AuthStatus status, AuthMode mode) : EventArgs
{
    public AuthStatus Status { get; } = status;

    public AuthMode Mode { get; } = mode;
}

/// <summary>Sign-in state and flows. Implemented by HushMusic.Auth. Events may arrive on any thread.</summary>
public interface IAuthService
{
    AuthStatus Status { get; }

    AuthMode Mode { get; }

    event EventHandler<AuthStatusChangedEventArgs>? StatusChanged;

    /// <summary>Loads stored credentials at startup.</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores cookies captured from the WebView2 sign-in window (encrypted) and switches to cookie auth.
    /// Throws <see cref="HushException"/> if the required cookies (e.g. __Secure-3PAPISID / SAPISID) are missing.
    /// </summary>
    Task SignInWithCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken cancellationToken = default);

    /// <summary>Fallback sign-in: the raw <c>Cookie</c> request header copied from a signed-in music.youtube.com browser tab.</summary>
    Task SignInWithCookieHeaderAsync(string cookieHeader, CancellationToken cancellationToken = default);

    /// <summary>Signs out and wipes every stored credential.</summary>
    Task SignOutAsync(CancellationToken cancellationToken = default);
}

/// <summary>Adds the current credentials to outgoing InnerTube requests. Implemented by HushMusic.Auth.</summary>
public interface IRequestAuthenticator
{
    bool IsAuthenticated { get; }

    AuthMode Mode { get; }

    /// <summary>Adds Cookie, Authorization (SAPISIDHASH) and X-Goog-AuthUser headers when signed in.</summary>
    Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lets the authenticator persist rotated session cookies from <c>Set-Cookie</c> response headers.
    /// (Our addition: ytmusicapi ignores Set-Cookie in cookie mode.)
    /// </summary>
    Task OnResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called by the InnerTube layer when YouTube Music treats an authenticated request as signed out
    /// (HTTP 401, "You must be signed in", sign-in prompt instead of data). Moves the status to Expired.
    /// </summary>
    void ReportSessionRejected(string reason);
}

/// <summary>Gives yt-dlp the signed-in cookies. Implemented by HushMusic.Auth.</summary>
public interface ICookieFileProvider
{
    /// <summary>
    /// yt-dlp only accepts cookies as a file, so this is the one place cookies touch disk unencrypted:
    /// a Netscape-format temp file readable only by the current user. Dispose the result as soon as
    /// yt-dlp exits to delete it. Returns null when not signed in.
    /// </summary>
    Task<TemporaryFile?> CreateNetscapeCookieFileAsync(CancellationToken cancellationToken = default);
}

/// <summary>A file that is deleted (best effort) on dispose.</summary>
public sealed class TemporaryFile(string path) : IDisposable
{
    public string Path { get; } = path;

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
