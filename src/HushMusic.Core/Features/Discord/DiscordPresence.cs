namespace HushMusic.Core.Features.Discord;

/// <summary>The Discord application Hush shows up as.</summary>
public static class DiscordPresence
{
    /// <summary>
    /// The Application ID of the "Hush" application in the Discord Developer Portal (discord.com/developers/applications).
    /// Discord shows its name and icon ("Listening to Hush"). Without a valid id the Discord status does nothing.
    /// </summary>
    public const string ApplicationId = "1569738362468831262";

    /// <summary>Application ids are Discord snowflakes: 17 to 20 digits.</summary>
    public static bool IsValidApplicationId(string? id) =>
        id is { Length: >= 17 and <= 20 } && id.All(char.IsAsciiDigit);
}

/// <summary>Where the Discord status connects, and as which application. The app uses <see cref="FromEnvironment"/>.</summary>
public sealed record DiscordPresenceOptions
{
    /// <summary>The desktop app listens on <c>\\.\pipe\discord-ipc-0</c> … <c>-9</c> (one per running Discord client).</summary>
    public const string DefaultPipeName = "discord-ipc";

    /// <summary>The pipe name without the "-0" … "-9" suffix.</summary>
    public string PipeName { get; init; } = DefaultPipeName;

    public string ApplicationId { get; init; } = DiscordPresence.ApplicationId;

    /// <summary>False keeps the feature off whatever the setting says (automated test instances without a fake Discord).</summary>
    public bool Allowed { get; init; } = true;

    /// <summary>
    /// Normal runs use Discord's pipes. Automated test instances (HUSHMUSIC_TEST_BACKGROUND=1) never touch the user's real
    /// Discord, which would publish to their profile: only with HUSHMUSIC_TEST_DISCORD_PIPE naming a fake pipe, and then
    /// with HUSHMUSIC_TEST_DISCORD_APP_ID as the application id when it is set.
    /// </summary>
    public static DiscordPresenceOptions FromEnvironment() => From(Environment.GetEnvironmentVariable);

    internal static DiscordPresenceOptions From(Func<string, string?> variable)
    {
        if (variable("HUSHMUSIC_TEST_BACKGROUND") != "1")
        {
            return new DiscordPresenceOptions();
        }

        var pipe = variable("HUSHMUSIC_TEST_DISCORD_PIPE");
        if (string.IsNullOrWhiteSpace(pipe) || pipe.StartsWith(DefaultPipeName, StringComparison.OrdinalIgnoreCase))
        {
            return new DiscordPresenceOptions { Allowed = false };
        }

        return new DiscordPresenceOptions
        {
            PipeName = pipe,
            ApplicationId = variable("HUSHMUSIC_TEST_DISCORD_APP_ID") is { Length: > 0 } id ? id : DiscordPresence.ApplicationId,
        };
    }
}
