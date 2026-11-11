using HushMusic.Core.Models;

namespace HushMusic.Core.Abstractions;

/// <summary>
/// The "Listening to" status on the Discord desktop app (<see cref="AppSettings.ShowOnDiscord"/>), set through Discord's
/// local RPC pipe. Nothing runs while the setting is off. Implemented in Core; <see cref="ConnectionChanged"/> is raised on
/// a background thread.
/// </summary>
public interface IDiscordPresence
{
    /// <summary>True while Hush is connected to the Discord desktop app.</summary>
    bool IsConnected { get; }

    event EventHandler? ConnectionChanged;
}

/// <summary>An album, single or EP that showed up on the page of an artist the user follows.</summary>
/// <param name="Album">The release; its <see cref="Album.Artists"/> name the followed artist.</param>
public sealed record NewRelease(string ArtistId, string ArtistName, Album Album);

public sealed class NewReleasesFoundEventArgs(IReadOnlyList<NewRelease> releases) : EventArgs
{
    /// <summary>Newest first, never empty.</summary>
    public IReadOnlyList<NewRelease> Releases { get; } = releases;
}

/// <summary>
/// New-release alerts (<see cref="AppSettings.NewReleaseAlerts"/>): once a day, slowly, Hush reads the pages of the
/// artists the signed-in account subscribes to and looks for albums and singles it hasn't seen there before. Nothing runs
/// while the setting is off or nobody is signed in. Implemented in Core; <see cref="Found"/> is raised on a background
/// thread.
/// </summary>
public interface INewReleaseAlerts
{
    /// <summary>
    /// New releases, once a round of checks is done. An artist's first check only records what is already out, so it never
    /// reports anything.
    /// </summary>
    event EventHandler<NewReleasesFoundEventArgs>? Found;
}
