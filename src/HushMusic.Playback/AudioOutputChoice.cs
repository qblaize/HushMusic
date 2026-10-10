namespace HushMusic.Playback;

/// <summary>An audio output (render endpoint) as Windows names it.</summary>
/// <param name="IsConnected">False for a chosen output that is unplugged or disabled.</param>
public sealed record AudioOutputDevice(string Id, string Name, bool IsConnected = true);

/// <summary>Which output plays, and what the output picker lists (<see cref="Core.Abstractions.AppSettings.AudioOutputDeviceId"/>).</summary>
public static class AudioOutputChoice
{
    /// <summary>A saved choice: null or blank is the system default.</summary>
    public static string? Normalize(string? deviceId) => string.IsNullOrWhiteSpace(deviceId) ? null : deviceId.Trim();

    /// <summary>Device ids are interface paths, which Windows compares without case.</summary>
    public static bool SameDevice(string? a, string? b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The output to play on: the chosen one while it is connected (spelled as the connected list has it), otherwise
    /// null, the system default. The choice itself is kept, so the output is used again as soon as it is back.
    /// </summary>
    public static string? Effective(string? chosenId, IEnumerable<string> connectedIds)
    {
        var chosen = Normalize(chosenId);
        return chosen is null ? null : connectedIds.FirstOrDefault(id => SameDevice(id, chosen));
    }

    /// <summary>
    /// What a picker lists after "System default": the connected outputs by name, then the chosen one when it isn't
    /// connected, under its last known name (empty when Windows doesn't know it any more).
    /// </summary>
    public static IReadOnlyList<AudioOutputDevice> ForPicker(IEnumerable<AudioOutputDevice> connected, string? chosenId, string? chosenName)
    {
        List<AudioOutputDevice> list =
        [
            .. connected
                .Where(d => d.IsConnected && Normalize(d.Id) is not null)
                .DistinctBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase),
        ];

        var chosen = Normalize(chosenId);
        if (chosen is not null && !list.Exists(d => SameDevice(d.Id, chosen)))
        {
            list.Add(new AudioOutputDevice(chosen, chosenName?.Trim() ?? string.Empty, IsConnected: false));
        }

        return list;
    }
}
