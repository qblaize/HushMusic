namespace HushMusic.App.Services.Updates;

public enum UpdateState
{
    /// <summary>Not installed with the installer (a development build, or a copied folder): nothing to update.</summary>
    NotInstalled,

    /// <summary>Installed, but this build has no update source (Updates:GitHubRepository is empty).</summary>
    Disabled,

    /// <summary>Installed; the first check hasn't run yet.</summary>
    Idle,

    Checking,

    UpToDate,

    Downloading,

    /// <summary>A newer version is downloaded and installs on <see cref="IUpdateService.RestartToUpdate"/>.</summary>
    ReadyToRestart,

    Failed,
}

/// <summary>
/// App updates from GitHub Releases (Velopack). Checks shortly after startup and then every five minutes, downloads a
/// newer version in the background and asks before restarting into it (in the app and with a Windows notification).
/// Only does anything in installed builds.
/// </summary>
public interface IUpdateService
{
    /// <summary>The running version, e.g. "0.1.0".</summary>
    string CurrentVersion { get; }

    UpdateState State { get; }

    /// <summary>True in installed builds with a working update source: <see cref="CheckAsync"/> can do something.</summary>
    bool CanCheck { get; }

    /// <summary>The version being downloaded or ready to install.</summary>
    string? AvailableVersion { get; }

    /// <summary>0–100 while <see cref="UpdateState.Downloading"/>.</summary>
    int DownloadProgress { get; }

    /// <summary>Why the last check failed (<see cref="UpdateState.Failed"/>).</summary>
    string? ErrorMessage { get; }

    /// <summary>Raised on a background thread whenever any of the above changes.</summary>
    event EventHandler? StateChanged;

    /// <summary>Checks now and downloads a newer version if there is one. Returns the resulting state; never throws except on cancellation.</summary>
    Task<UpdateState> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Quits the app; the updater installs the downloaded version and starts it again.</summary>
    void RestartToUpdate();
}
