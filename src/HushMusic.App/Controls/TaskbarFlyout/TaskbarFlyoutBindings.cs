using HushMusic.Core.Models;

namespace HushMusic.App.Controls.TaskbarFlyout;

/// <summary>x:Bind function helpers for <see cref="TaskbarFlyoutView"/>, also used by the taskbar player.</summary>
public static class TaskbarFlyoutBindings
{
    public static string TitleOf(Track? track) => track?.Title ?? "Not playing";

    public static string ArtistsOf(Track? track) => track?.ArtistsText ?? string.Empty;

    /// <summary>A live stream (internet radio station): no progress to show.</summary>
    public static bool IsLive(Track? track) => track?.IsLiveRadio == true;

    /// <summary>The seek bar only shows when there is something to seek in.</summary>
    public static Visibility SeekVisibility(Track? track, double durationSeconds) =>
        durationSeconds > 0 && !IsLive(track) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility LiveVisibility(Track? track) => IsLive(track) ? Visibility.Visible : Visibility.Collapsed;

    public static string VolumeText(double volume, bool muted) => muted ? "Muted" : $"{Math.Round(Math.Clamp(volume, 0, 100)):0}%";

    /// <summary>The lower area shows when something comes next, or the song can have lyrics (its tabs then show).</summary>
    public static Visibility LowerAreaVisibility(bool hasUpNext, bool lyricsAvailable) =>
        hasUpNext || lyricsAvailable ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The Up next tab is chosen but nothing is queued after this song.</summary>
    public static Visibility NothingNextVisibility(bool hasUpNext, bool lyricsAvailable, bool lyricsOn) =>
        !hasUpNext && lyricsAvailable && !lyricsOn ? Visibility.Visible : Visibility.Collapsed;
}
