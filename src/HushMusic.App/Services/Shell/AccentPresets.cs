using Microsoft.UI;
using Windows.UI;

namespace HushMusic.App.Services.Shell;

/// <summary>A fixed accent the user can pick instead of the album-art colour.</summary>
/// <param name="Name">Stored in <see cref="Core.Abstractions.AppSettings.AccentStyle"/>.</param>
/// <param name="Dark">The accent on the dark theme.</param>
/// <param name="Light">The same hue tuned for legibility on the light theme.</param>
public sealed record AccentPreset(string Name, string Description, Color Dark, Color Light);

public static class AccentPresets
{
    /// <summary><see cref="Core.Abstractions.AppSettings.AccentStyle"/> value for "follow the album art".</summary>
    public const string Artwork = "Artwork";

    /// <summary><see cref="Core.Abstractions.AppSettings.AccentStyle"/> value for "the Windows accent colour".</summary>
    public const string System = "System";

    // Contrast as accent text: dark variants >= 5:1 on #0B0B0C, light variants >= 4.3:1 on #FFFFFF (and white text on them).
    public static IReadOnlyList<AccentPreset> All { get; } =
    [
        new("Crimson", "Warm red", Rgb(0xE5, 0x48, 0x4D), Rgb(0xD1, 0x2F, 0x37)),
        new("Forest", "Deep green", Rgb(0x30, 0xA4, 0x6C), Rgb(0x1F, 0x8A, 0x55)),
        new("Frost", "Icy blue", Rgb(0x4C, 0xC3, 0xFF), Rgb(0x00, 0x7F, 0xC4)),
        new("Ember", "Burnt orange", Rgb(0xFF, 0x7A, 0x1A), Rgb(0xC7, 0x54, 0x00)),
        new("Iris", "Soft violet", Rgb(0x9D, 0x7B, 0xFA), Rgb(0x6D, 0x3F, 0xE0)),
    ];

    /// <summary>Null for <see cref="Artwork"/>, <see cref="System"/> or an unknown name.</summary>
    public static AccentPreset? Find(string? name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public static bool IsSystem(string? name) => string.Equals(name, System, StringComparison.OrdinalIgnoreCase);

    /// <summary>The stored value in its canonical spelling; anything unknown means <see cref="Artwork"/>.</summary>
    public static string Normalize(string? name) => IsSystem(name) ? System : Find(name)?.Name ?? Artwork;

    private static Color Rgb(byte r, byte g, byte b) => ColorHelper.FromArgb(0xFF, r, g, b);
}
