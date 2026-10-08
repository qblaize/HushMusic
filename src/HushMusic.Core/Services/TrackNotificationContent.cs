using System.Xml.Linq;
using HushMusic.Core.Models;

namespace HushMusic.Core.Services;

/// <summary>What a button or a click on the song notification asks for.</summary>
public enum TrackNotificationCommand
{
    None,

    /// <summary>The notification itself was clicked: show the window.</summary>
    Show,

    /// <summary>The Next button.</summary>
    Next,
}

/// <summary>The song-change notification's text and cover.</summary>
/// <param name="Key">Identifies what it is about (the song, or the station plus the song on air), so the same one isn't shown twice.</param>
/// <param name="Title">The song, or the station while its song is unknown.</param>
/// <param name="Subtitle">The artists, or "Artist · Station" for radio.</param>
/// <param name="Attribution">A small third line (the album), or null.</param>
/// <param name="ImageUrl">The cover to show, or null.</param>
public sealed record TrackNotification(string Key, string Title, string Subtitle, string? Attribution, string? ImageUrl)
{
    /// <summary>A track that started playing; for a live station, what it is playing now (<paramref name="onAir"/> may be null).</summary>
    public static TrackNotification For(Track track, RadioNowPlaying? onAir = null, string? imageUrl = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.IsLiveRadio)
        {
            var song = LiveRadio.IsFor(track, onAir) ? onAir : null;
            return new TrackNotification(
                $"{track.VideoId}|{song?.StreamTitle}",
                LiveRadio.DisplayTitle(track, song),
                LiveRadio.DisplaySubtitle(track, song),
                null,
                song?.ArtworkUrl ?? imageUrl);
        }

        return new TrackNotification(track.VideoId, track.Title, track.ArtistsText, track.Album?.Name, imageUrl);
    }
}

/// <summary>
/// Builds the Windows notification (toast) XML for <see cref="TrackNotification"/> and reads its arguments back. Pure
/// logic: the App shows it with the Windows App SDK's AppNotificationManager.
/// </summary>
public static class TrackNotificationContent
{
    public const string ShowArgument = "action=show";
    public const string NextArgument = "action=next";

    /// <summary>
    /// The toast: title, subtitle, optional attribution, the cover as the app logo (square, from a local file) and a
    /// Next button. Silent, because music is already playing.
    /// </summary>
    /// <param name="imagePath">A local image file, or null for no cover.</param>
    public static string Build(TrackNotification notification, string? imagePath, bool nextButton)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var binding = new XElement(
            "binding",
            new XAttribute("template", "ToastGeneric"),
            new XElement("text", new XAttribute("hint-maxLines", "1"), notification.Title),
            new XElement("text", notification.Subtitle));
        if (!string.IsNullOrWhiteSpace(notification.Attribution))
        {
            binding.Add(new XElement("text", new XAttribute("placement", "attribution"), notification.Attribution));
        }

        if (!string.IsNullOrEmpty(imagePath) && Path.IsPathFullyQualified(imagePath))
        {
            binding.Add(new XElement(
                "image",
                new XAttribute("placement", "appLogoOverride"),
                new XAttribute("hint-crop", "none"),
                new XAttribute("src", new Uri(imagePath).AbsoluteUri)));
        }

        var toast = new XElement(
            "toast",
            new XAttribute("launch", ShowArgument),
            new XElement("visual", binding));
        if (nextButton)
        {
            toast.Add(new XElement(
                "actions",
                new XElement("action", new XAttribute("content", "Next"), new XAttribute("arguments", NextArgument))));
        }

        toast.Add(new XElement("audio", new XAttribute("silent", "true")));
        return toast.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>The command in a notification's activation arguments (the launch or button arguments).</summary>
    public static TrackNotificationCommand Parse(string? arguments) => arguments?.Trim() switch
    {
        NextArgument => TrackNotificationCommand.Next,
        ShowArgument or "" or null => TrackNotificationCommand.Show,
        _ => TrackNotificationCommand.None,
    };
}
