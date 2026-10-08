namespace HushMusic.Core.Abstractions;

public enum NotificationSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

public sealed record AppNotification(NotificationSeverity Severity, string Title, string Message, Exception? Exception = null)
{
    /// <summary>An optional button on the notification (e.g. "Restart"). A notification with an action stays until it is used or closed.</summary>
    public NotificationAction? Action { get; init; }
}

/// <summary>A notification's button. <see cref="Invoke"/> runs on the UI thread.</summary>
public sealed record NotificationAction(string Label, Action Invoke);

/// <summary>User-visible messages. The shell shows each one as an InfoBar. Thread-safe.</summary>
public interface INotificationService
{
    event EventHandler<AppNotification>? Raised;

    void Show(AppNotification notification);

    void ShowError(string title, Exception exception);

    void ShowInfo(string title, string message);
}

/// <summary>Persisted, non-secret user settings.</summary>
public sealed class AppSettings
{
    /// <summary>0.0 – 1.0.</summary>
    public double Volume { get; set; } = 0.8;

    /// <summary>Full path to yt-dlp.exe. Null uses the copy managed by the app in LocalAppData.</summary>
    public string? YtDlpPath { get; set; }

    public bool CheckYtDlpUpdatesOnStartup { get; set; } = true;

    /// <summary>
    /// The JavaScript runtime yt-dlp uses to solve YouTube's playback challenges. "auto" (or empty): the app's own Deno,
    /// downloaded on first use, with Node.js from PATH standing in until it is ready. "node" / "deno": that runtime from
    /// PATH, or "auto" when it is not installed ("node" was the default before the app managed Deno, so a saved "node"
    /// keeps working without Node.js). Anything else, e.g. "node:C:\path\to\node.exe" or "deno:C:\path\to\deno.exe", goes to
    /// yt-dlp's <c>--js-runtimes</c> unchanged.
    /// </summary>
    public string YtDlpJsRuntime { get; set; } = "auto";

    /// <summary>
    /// Pass the signed-in cookies to yt-dlp. Off by default: yt-dlp warns that accounts used with it
    /// can get rate-limited or blocked, and anonymous resolution works for normal tracks.
    /// </summary>
    public bool UseAccountForStreams { get; set; }

    /// <summary>InnerTube "gl" country code (e.g. "RO"). Empty uses ytmusicapi's default.</summary>
    public string ContentLocation { get; set; } = string.Empty;

    public bool ReportPlaybackHistory { get; set; } = true;

    /// <summary>Serilog minimum level: Verbose, Debug, Information, Warning, Error.</summary>
    public string LogLevel { get; set; } = "Information";

    /// <summary>"Dark", "Light" or "System" (follow the Windows app mode).</summary>
    public string Theme { get; set; } = "Dark";

    /// <summary>"Artwork" (taken from the current album art) or a preset name: Crimson, Forest, Frost, Ember, Iris.</summary>
    public string AccentStyle { get; set; } = "Artwork";

    /// <summary>
    /// Now Playing artwork: "CoverFlow" (the songs before and after the current one stacked beside it) or "Single"
    /// (just the current cover).
    /// </summary>
    public string NowPlayingArtStyle { get; set; } = "CoverFlow";

    /// <summary>Cover Flow mode only: the Up next / Lyrics / Related panel is hidden so the covers get the whole view.</summary>
    public bool NowPlayingPanelHidden { get; set; }

    /// <summary>"Hush" (the app's own design) or "Windows" (stock Windows 11 / Fluent look with Mica). Applied at startup.</summary>
    public string DesignSystem { get; set; } = "Hush";

    /// <summary>"Standard" or "Minimal" (a calmer, docked player bar and a plain Now Playing view).</summary>
    public string PlayerLayout { get; set; } = "Standard";

    /// <summary>Evens out loudness between tracks using YouTube's per-track loudness (only ever turns loud tracks down).</summary>
    public bool NormalizeVolume { get; set; } = true;

    /// <summary>Restore the last queue, track and position (paused) when the app starts.</summary>
    public bool ResumeLastSession { get; set; } = true;

    /// <summary>Closing the window keeps the app (and the music) running in the notification area.</summary>
    public bool CloseToTray { get; set; }

    /// <summary>Start with Windows (minimized to the notification area). Mirrored to the HKCU Run key by the app.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>A small player in the empty space of the primary taskbar (Windows 11), with a flyout for the full controls.</summary>
    public bool ShowTaskbarWidget { get; set; }

    /// <summary>Most recent first, at most 8. Local only.</summary>
    public List<string> RecentSearches { get; set; } = [];

    /// <summary>Scrobble to Last.fm while a Last.fm account is connected.</summary>
    public bool LastFmScrobbling { get; set; } = true;
}

/// <summary>Small named secrets (API keys, session keys), encrypted for the current Windows user. Implemented in HushMusic.Auth.</summary>
public interface ISecretStore
{
    /// <summary>Returns null when the secret is missing or cannot be decrypted.</summary>
    Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>A null value deletes the secret.</summary>
    Task WriteAsync(string name, string? value, CancellationToken cancellationToken = default);
}

public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies <paramref name="update"/>, saves to disk and raises <see cref="Changed"/>.</summary>
    Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default);
}

/// <summary>Well-known folders under %LOCALAPPDATA%\HushMusic. All are created on first access.</summary>
public interface IAppPaths
{
    string Root { get; }

    string Logs { get; }

    string Cache { get; }

    string ImageCache { get; }

    /// <summary>Encrypted credential blobs.</summary>
    string Secure { get; }

    /// <summary>Managed tools such as yt-dlp.exe.</summary>
    string Tools { get; }

    string SettingsFile { get; }
}
