using Microsoft.Extensions.Logging;
using HushMusic.Auth.Cookies;
using HushMusic.Auth.Storage;
using HushMusic.Core;
using HushMusic.Core.Abstractions;

namespace HushMusic.Auth;

/// <summary>
/// Cookie sign-in for YouTube Music (the only auth InnerTube accepts today), DPAPI-encrypted session storage,
/// per-request SAPISIDHASH signing and the yt-dlp cookie file. One singleton serves all three interfaces.
/// Thread-safe. Cookie values and hashes are never logged.
/// </summary>
public sealed class AuthService : IAuthService, IRequestAuthenticator, ICookieFileProvider
{
    private const string SessionFileName = "session.bin";
    private const string CookieTempFolderName = "tmp";

    /// <summary>Rotated cookies are written to disk at most this often.</summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(30);

    private readonly IAppPaths _paths;
    private readonly ILogger<AuthService> _logger;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _storeLock = new(1, 1);
    private EncryptedSessionStore? _store;

    // Guarded by _stateLock.
    private Session? _session;
    private AuthStatus _status = AuthStatus.SignedOut;
    private long _generation;
    private bool _persistScheduled;
    private long? _lastPersistTick;

    private int _initialized;

    public AuthService(IAppPaths paths, ILogger<AuthService> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _paths = paths;
        _logger = logger;
    }

    public event EventHandler<AuthStatusChangedEventArgs>? StatusChanged;

    public AuthStatus Status
    {
        get
        {
            lock (_stateLock)
            {
                return _status;
            }
        }
    }

    /// <summary>Cookies while signed in or expired (credentials are still stored), otherwise None.</summary>
    public AuthMode Mode
    {
        get
        {
            lock (_stateLock)
            {
                return _status is AuthStatus.SignedIn or AuthStatus.Expired ? AuthMode.Cookies : AuthMode.None;
            }
        }
    }

    /// <summary>False while Expired, so InnerTube sends anonymous requests and library calls fail fast.</summary>
    public bool IsAuthenticated => CurrentSignedInSession() is not null;

    /// <summary>The mode actually applied to requests: None while Expired.</summary>
    AuthMode IRequestAuthenticator.Mode => IsAuthenticated ? AuthMode.Cookies : AuthMode.None;

    private EncryptedSessionStore Store =>
        _store ??= new EncryptedSessionStore(Path.Combine(_paths.Secure, SessionFileName), _logger);

    private string CookieTempFolder => Path.Combine(_paths.Secure, CookieTempFolderName);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        long generation;
        lock (_stateLock)
        {
            generation = _generation;
        }

        IReadOnlyList<StoredCookie>? cookies;
        try
        {
            // Leftovers of a crash or of yt-dlp runs that never disposed their file.
            await Task.Run(DeleteCookieTempFiles, cancellationToken).ConfigureAwait(false);

            await _storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cookies = await Store.LoadAsync(cancellationToken).ConfigureAwait(false);
                if (cookies is not null && CookieJar.FindSapisid(cookies) is null)
                {
                    _logger.LogWarning("The stored session has no SAPISID cookie; deleting it and continuing signed out.");
                    Store.Delete();
                    cookies = null;
                }
            }
            finally
            {
                _storeLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            Volatile.Write(ref _initialized, 0);
            throw;
        }

        if (cookies is null)
        {
            _logger.LogInformation("No stored YouTube Music session; signed out.");
            return;
        }

        lock (_stateLock)
        {
            // A sign-in or sign-out that ran meanwhile wins over what was on disk.
            if (_generation != generation || _session is not null)
            {
                return;
            }

            _session = new Session(cookies);
            _status = AuthStatus.SignedIn;
        }

        _logger.LogInformation("Restored the stored YouTube Music session ({CookieCount} cookies).", cookies.Count);
        RaiseStatusChanged(AuthStatus.SignedIn, AuthMode.Cookies);
    }

    public Task SignInWithCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cookies);

        var jar = CookieJar.FromBrowserCookies(cookies, out var skipped);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "Sign-in window returned {Total} cookies; keeping {Kept} for youtube.com ({Skipped} foreign, duplicate or invalid): {Names}.",
                cookies.Count,
                jar.Count,
                skipped,
                string.Join(", ", jar.Select(c => c.Name)));
        }

        if (CookieJar.FindSapisid(jar) is null)
        {
            throw new HushException(
                "Sign-in did not finish: YouTube Music did not return the session cookies (__Secure-3PAPISID / SAPISID). "
                + "Sign in again and wait until YouTube Music has loaded.");
        }

        return CompleteSignInAsync(jar, cancellationToken);
    }

    public Task SignInWithCookieHeaderAsync(string cookieHeader, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            throw new HushException("The cookie header is empty. Paste the Cookie header of a request to music.youtube.com.");
        }

        var jar = CookieJar.ParseCookieHeader(cookieHeader);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("Parsed {Count} cookies from the pasted header: {Names}.", jar.Count, string.Join(", ", jar.Select(c => c.Name)));
        }

        if (CookieJar.FindSapisid(jar) is null)
        {
            throw new HushException(
                "The pasted cookies are missing __Secure-3PAPISID (or SAPISID). Copy the full Cookie header of a "
                + "request to music.youtube.com from a browser tab where you are signed in.");
        }

        return CompleteSignInAsync(jar, cancellationToken);
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        // Once started, sign-out always completes: a half-done wipe would leave credentials behind.
        cancellationToken.ThrowIfCancellationRequested();

        AuthStatus previous;
        lock (_stateLock)
        {
            previous = _status;
            _generation++;
            _session = null;
            _status = AuthStatus.SignedOut;
        }

        await _storeLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await Task.Run(Store.Delete, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _storeLock.Release();
        }

        await Task.Run(DeleteCookieTempFiles, CancellationToken.None).ConfigureAwait(false);
        _logger.LogInformation("Signed out; stored YouTube Music credentials were deleted.");

        if (previous != AuthStatus.SignedOut)
        {
            RaiseStatusChanged(AuthStatus.SignedOut, AuthMode.None);
        }
    }

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Checked even when signed out so a caller bug shows up without an account.
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri)
        {
            throw new ArgumentException("ApplyAsync needs a request with an absolute URI.", nameof(request));
        }

        var session = CurrentSignedInSession();
        if (session is null)
        {
            return Task.CompletedTask;
        }

        // Credentials only ever go to https://*.youtube.com, whoever asks.
        if (uri.Scheme != Uri.UriSchemeHttps || !CookieJar.IsYouTubeHost(uri.Host))
        {
            _logger.LogDebug("Not attaching YouTube credentials to a request for {Host}.", uri.Host);
            return Task.CompletedTask;
        }

        var headers = request.Headers;

        // Like ytmusicapi (requests): an explicit Cookie header replaces anything else, e.g. the anonymous SOCS=CAI.
        headers.Remove("Cookie");
        headers.TryAddWithoutValidation("Cookie", session.CookieHeader);

        headers.Remove("Authorization");
        headers.TryAddWithoutValidation(
            "Authorization",
            SapisidHash.Compute(session.Sapisid, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

        // Index of the account in Google's multi-login session; the sign-in window holds a single account.
        headers.Remove("X-Goog-AuthUser");
        headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");

        // Part of every cookie-mode header set ytmusicapi users copy from the browser (headers_auth.json.example).
        if (!headers.Contains("X-Origin"))
        {
            headers.TryAddWithoutValidation("X-Origin", SapisidHash.Origin);
        }

        return Task.CompletedTask;
    }

    public Task OnResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);

        var uri = response.RequestMessage?.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !CookieJar.IsYouTubeHost(uri.Host)
            || !response.Headers.TryGetValues("Set-Cookie", out var setCookieHeaders))
        {
            return Task.CompletedTask;
        }

        var now = DateTimeOffset.UtcNow;
        var updates = new List<SetCookie>();
        foreach (var header in setCookieHeaders)
        {
            if (SetCookieParser.TryParse(header, now, out var parsed) && !parsed.IsDeletion && parsed.Value.Length > 0)
            {
                updates.Add(parsed);
            }
        }

        if (updates.Count == 0)
        {
            return Task.CompletedTask;
        }

        List<string>? rotated = null;
        lock (_stateLock)
        {
            if (_status != AuthStatus.SignedIn || _session is null)
            {
                return Task.CompletedTask;
            }

            List<StoredCookie>? cookies = null;
            foreach (var update in updates)
            {
                var source = (IReadOnlyList<StoredCookie>?)cookies ?? _session.Cookies;
                var index = FindRotationTarget(source, update, uri.Host);
                if (index < 0)
                {
                    continue;
                }

                var existing = source[index];
                var value = CookieJar.SanitizeValue(update.Value);
                if (value == existing.Value)
                {
                    continue;
                }

                cookies ??= [.. _session.Cookies];
                cookies[index] = existing with { Value = value, Expires = update.Expires ?? existing.Expires };
                (rotated ??= []).Add(existing.Name);
            }

            // A rotation that would leave nothing to hash (e.g. a quoted empty SAPISID) is not applied.
            if (cookies is null || CookieJar.FindSapisid(cookies) is null)
            {
                return Task.CompletedTask;
            }

            _session = new Session(cookies);
            SchedulePersistLocked();
        }

        _logger.LogDebug("YouTube rotated session cookies: {Names}.", string.Join(", ", rotated!));
        return Task.CompletedTask;
    }

    public void ReportSessionRejected(string reason)
    {
        bool changed;
        lock (_stateLock)
        {
            changed = _status == AuthStatus.SignedIn;
            if (changed)
            {
                _status = AuthStatus.Expired;
            }
        }

        if (!changed)
        {
            _logger.LogDebug("Session rejection reported while not signed in ({Reason}); ignored.", reason);
            return;
        }

        _logger.LogWarning("YouTube Music rejected the stored session: {Reason}. Marked as expired; sign in again.", reason);
        RaiseStatusChanged(AuthStatus.Expired, AuthMode.Cookies);
    }

    public async Task<TemporaryFile?> CreateNetscapeCookieFileAsync(CancellationToken cancellationToken = default)
    {
        var session = CurrentSignedInSession();
        if (session is null)
        {
            return null;
        }

        var folder = Directory.CreateDirectory(CookieTempFolder).FullName;
        var path = Path.Combine(folder, $"cookies-{Guid.NewGuid():N}.txt");
        try
        {
            await NetscapeCookieFile.WriteRestrictedAsync(path, NetscapeCookieFile.Format(session.Cookies), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            new TemporaryFile(path).Dispose();
            if (ex is OperationCanceledException)
            {
                throw;
            }

            throw new HushException("Could not write the cookie file for yt-dlp.", ex);
        }

        return new TemporaryFile(path);
    }

    private async Task CompleteSignInAsync(List<StoredCookie> jar, CancellationToken cancellationToken)
    {
        await _storeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await Store.SaveAsync(jar, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError("Could not store the session ({ExceptionType}, 0x{HResult:X8}).", ex.GetType().Name, ex.HResult);
                throw new HushException("Could not save the sign-in securely on this PC.", ex);
            }

            lock (_stateLock)
            {
                _generation++;
                _session = new Session(jar);
                _status = AuthStatus.SignedIn;
                _lastPersistTick = Environment.TickCount64;
            }
        }
        finally
        {
            _storeLock.Release();
        }

        // Cookie files of a previous account must not outlive the switch.
        DeleteCookieTempFiles();
        _logger.LogInformation("Signed in to YouTube Music with cookies ({CookieCount} cookies stored encrypted).", jar.Count);

        // Raised even when already signed in: an account switch is a status change for listeners.
        RaiseStatusChanged(AuthStatus.SignedIn, AuthMode.Cookies);
    }

    private Session? CurrentSignedInSession()
    {
        lock (_stateLock)
        {
            return _status == AuthStatus.SignedIn ? _session : null;
        }
    }

    /// <summary>
    /// Index of the jar cookie a Set-Cookie may overwrite: same name, same domain, compatible path.
    /// Never matches a cookie we don't already hold, so rotation can't add cookies or cross domains.
    /// </summary>
    private static int FindRotationTarget(IReadOnlyList<StoredCookie> cookies, SetCookie update, string requestHost)
    {
        string domain;
        if (update.Domain is null)
        {
            domain = requestHost.ToLowerInvariant();
        }
        else
        {
            var bare = update.Domain.TrimStart('.');
            var hostMatches = requestHost.Equals(bare, StringComparison.OrdinalIgnoreCase)
                || requestHost.EndsWith("." + bare, StringComparison.OrdinalIgnoreCase);
            if (!hostMatches || !CookieJar.IsYouTubeCookieDomain(update.Domain))
            {
                return -1;
            }

            domain = update.Domain;
        }

        for (var i = 0; i < cookies.Count; i++)
        {
            var cookie = cookies[i];
            if (cookie.Name.Equals(update.Name, StringComparison.Ordinal)
                && cookie.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase)
                && (update.Path is null || update.Path == cookie.Path))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Leading write right away, then at most one write per <see cref="PersistInterval"/>.</summary>
    private void SchedulePersistLocked()
    {
        if (_persistScheduled)
        {
            return;
        }

        _persistScheduled = true;
        var elapsed = _lastPersistTick is { } last ? Environment.TickCount64 - last : long.MaxValue;
        var delay = elapsed >= (long)PersistInterval.TotalMilliseconds
            ? TimeSpan.Zero
            : PersistInterval - TimeSpan.FromMilliseconds(elapsed);
        _ = Task.Run(() => PersistRotatedCookiesAsync(delay));
    }

    private async Task PersistRotatedCookiesAsync(TimeSpan delay)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay).ConfigureAwait(false);
            }

            await _storeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                Session? current;
                lock (_stateLock)
                {
                    _persistScheduled = false;
                    current = _session;
                }

                // Signed out meanwhile: never resurrect the blob.
                if (current is null)
                {
                    return;
                }

                await Store.SaveAsync(current.Cookies, CancellationToken.None).ConfigureAwait(false);
                lock (_stateLock)
                {
                    _lastPersistTick = Environment.TickCount64;
                }
            }
            finally
            {
                _storeLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not save rotated session cookies ({ExceptionType}, 0x{HResult:X8}).", ex.GetType().Name, ex.HResult);
        }
    }

    private void DeleteCookieTempFiles()
    {
        try
        {
            var folder = CookieTempFolder;
            if (!Directory.Exists(folder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(folder))
            {
                // A file yt-dlp still has open fails here and is removed by its TemporaryFile or the next start.
                new TemporaryFile(file).Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Could not clean the cookie temp folder ({ExceptionType}).", ex.GetType().Name);
        }
    }

    private void RaiseStatusChanged(AuthStatus status, AuthMode mode)
    {
        try
        {
            StatusChanged?.Invoke(this, new AuthStatusChangedEventArgs(status, mode));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An AuthService.StatusChanged handler threw.");
        }
    }

    /// <summary>Immutable snapshot of the signed-in jar with the derived header values.</summary>
    private sealed class Session
    {
        public Session(IReadOnlyList<StoredCookie> cookies)
        {
            Cookies = cookies;
            CookieHeader = CookieJar.BuildCookieHeader(cookies);
            Sapisid = CookieJar.FindSapisid(cookies)
                ?? throw new InvalidOperationException("A session needs a SAPISID cookie.");
        }

        public IReadOnlyList<StoredCookie> Cookies { get; }

        public string CookieHeader { get; }

        public string Sapisid { get; }
    }
}
