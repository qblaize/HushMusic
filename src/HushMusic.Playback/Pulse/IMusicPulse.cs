namespace HushMusic.Playback.Pulse;

/// <summary>What the music is doing right now, for visuals. Each value is 0 – 1, relative to the music's own recent loudness.</summary>
/// <param name="Bass">Energy of the low end (kick drum and bass line): rises within a frame, falls over about a quarter of a second.</param>
/// <param name="Beat">Jumps on each kick or bass onset, then fades out over about half a second.</param>
/// <param name="Level">Overall loudness, smoothed over about half a second.</param>
public readonly record struct MusicPulseLevels(float Bass, float Beat, float Level);

public enum MusicPulseAvailability
{
    Available,

    /// <summary>Capturing one app's audio needs Windows 11 (Windows build 20348 or later).</summary>
    NeedsNewerWindows,

    /// <summary>Windows refused to capture the app's audio (this session).</summary>
    Failed,
}

/// <summary>
/// Listens to the audio Hush itself plays (songs and live radio, on whichever output device) and reports
/// <see cref="MusicPulseLevels"/> 25 times a second, for visuals that move with the music. Capture and analysis
/// run only while someone listens: no thread, no timer and no audio stream otherwise. Thread-safe.
/// </summary>
public interface IMusicPulse
{
    MusicPulseAvailability Availability { get; }

    /// <summary>Raised (on a background thread) when <see cref="Availability"/> changes.</summary>
    event EventHandler? AvailabilityChanged;

    /// <summary>
    /// Starts listening, if it isn't already, until the returned handle is disposed. <paramref name="levels"/> is called
    /// on the capture thread; keep it short. Does nothing unless <see cref="Availability"/> is
    /// <see cref="MusicPulseAvailability.Available"/>.
    /// </summary>
    IDisposable Listen(Action<MusicPulseLevels> levels);
}
