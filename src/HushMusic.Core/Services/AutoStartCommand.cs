namespace HushMusic.Core.Services;

/// <summary>What to do with the "start with Windows" Run entry.</summary>
public enum AutoStartAction
{
    None,
    Write,
    Delete,
}

/// <summary>
/// The command line the app registers to start with Windows, and the reconcile rule for the existing entry.
/// Pure logic (the registry access lives in the App).
/// </summary>
public static class AutoStartCommand
{
    /// <summary>Starts hidden in the notification area (or minimized when close-to-tray is off).</summary>
    public const string BackgroundArgument = "--background";

    /// <summary><c>"C:\path\HushMusic.exe" --background</c>.</summary>
    public static string Build(string executablePath) => $"\"{executablePath}\" {BackgroundArgument}";

    /// <summary>True when the arguments ask for a background start.</summary>
    public static bool IsBackgroundLaunch(IEnumerable<string> arguments) =>
        arguments.Any(a => string.Equals(a.Trim(), BackgroundArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a whole command line (e.g. a redirected activation's arguments) asks for a background start.</summary>
    public static bool IsBackgroundLaunch(string? commandLine) =>
        commandLine is not null && IsBackgroundLaunch(Tokenize(commandLine));

    /// <summary>The executable a Run entry starts: the quoted first token, or everything up to ".exe".</summary>
    public static string? ExecutableOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe > 0 ? text[..(exe + 4)] : text.Split(' ', 2)[0];
    }

    /// <summary>
    /// Enabled: the entry must be exactly <see cref="Build"/> for this executable (written again if missing, changed
    /// or pointing at an old location). Disabled: the entry is deleted only when it starts this executable, so
    /// another copy of the app (e.g. a test build) never removes the entry of the copy in use.
    /// </summary>
    public static AutoStartAction Reconcile(bool enabled, string? currentValue, string executablePath)
    {
        if (enabled)
        {
            return string.Equals(currentValue, Build(executablePath), StringComparison.Ordinal) ? AutoStartAction.None : AutoStartAction.Write;
        }

        return currentValue is not null && SamePath(ExecutableOf(currentValue), executablePath) ? AutoStartAction.Delete : AutoStartAction.None;
    }

    private static bool SamePath(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<string> Tokenize(string commandLine)
    {
        var quoted = false;
        var start = 0;
        for (var i = 0; i <= commandLine.Length; i++)
        {
            if (i < commandLine.Length && commandLine[i] == '"')
            {
                quoted = !quoted;
            }
            else if (i == commandLine.Length || (!quoted && char.IsWhiteSpace(commandLine[i])))
            {
                if (i > start)
                {
                    yield return commandLine[start..i].Trim('"');
                }

                start = i + 1;
            }
        }
    }
}
