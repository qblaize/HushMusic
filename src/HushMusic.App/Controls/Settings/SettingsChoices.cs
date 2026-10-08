using Windows.UI;

namespace HushMusic.App.Controls.Settings;

/// <summary>One segment of a <see cref="SegmentedPicker"/>: the stored value and the text shown.</summary>
public sealed record SettingsChoice(string Value, string Label);

/// <summary>One circle of an <see cref="AccentSwatchPicker"/>.</summary>
/// <param name="Value">Stored in AppSettings.AccentStyle.</param>
/// <param name="Colors">One colour fills the circle; several make a diagonal gradient.</param>
/// <param name="IsArtwork">Shows a picture glyph: the accent follows the album art.</param>
public sealed record AccentSwatch(string Value, string Name, string Description, IReadOnlyList<Color> Colors, bool IsArtwork = false);
