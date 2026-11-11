using System.Xml.Linq;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features.NewReleases;

/// <summary>
/// The Windows notification for new releases: one release opens its album page, several open the list. Pure logic; the
/// App shows it with the system toast API and routes clicks back through <see cref="TryParse"/>.
/// </summary>
public static class NewReleaseNotificationContent
{
    public const string ListArgument = "action=new-releases";
    public const string AlbumArgumentPrefix = "action=new-release&album=";

    /// <summary>The title and the line under it.</summary>
    public static (string Title, string Message) Text(IReadOnlyList<NewRelease> releases)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentOutOfRangeException.ThrowIfZero(releases.Count);
        if (releases is [var only])
        {
            return (only.Album.Title, $"New {Kind(only.Album.Type)} from {only.ArtistName}");
        }

        var names = releases.Select(r => r.ArtistName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var from = names switch
        {
            [] => "From artists you follow",
            [var a] => $"From {a}",
            [var a, var b] => $"From {a} and {b}",
            [var a, var b, var c] => $"From {a}, {b} and {c}",
            [var a, var b, ..] => $"From {a}, {b} and {names.Count - 2} more",
        };
        return ($"{releases.Count} new releases from artists you follow", from);
    }

    /// <summary>The toast XML: the text, the first release's cover (a local file, or null) and where a click goes. Silent.</summary>
    public static string Build(IReadOnlyList<NewRelease> releases, string? imagePath)
    {
        var (title, message) = Text(releases);
        var binding = new XElement(
            "binding",
            new XAttribute("template", "ToastGeneric"),
            new XElement("text", new XAttribute("hint-maxLines", "1"), title),
            new XElement("text", message));
        if (!string.IsNullOrEmpty(imagePath) && Path.IsPathFullyQualified(imagePath))
        {
            binding.Add(new XElement(
                "image",
                new XAttribute("placement", "appLogoOverride"),
                new XAttribute("hint-crop", "none"),
                new XAttribute("src", new Uri(imagePath).AbsoluteUri)));
        }

        var launch = releases is [var only] ? AlbumArgumentPrefix + only.Album.BrowseId : ListArgument;
        var toast = new XElement(
            "toast",
            new XAttribute("launch", launch),
            new XElement("visual", binding),
            new XElement("audio", new XAttribute("silent", "true")));
        return toast.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>
    /// True when <paramref name="arguments"/> come from a new-release notification; <paramref name="albumId"/> is the album
    /// to open, or null for the list.
    /// </summary>
    public static bool TryParse(string? arguments, out string? albumId)
    {
        albumId = null;
        var text = arguments?.Trim();
        if (text == ListArgument)
        {
            return true;
        }

        if (text is not null && text.StartsWith(AlbumArgumentPrefix, StringComparison.Ordinal)
            && text[AlbumArgumentPrefix.Length..] is { Length: > 0 } id && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            albumId = id;
            return true;
        }

        return false;
    }

    private static string Kind(AlbumType type) => type switch
    {
        AlbumType.Single => "single",
        AlbumType.EP => "EP",
        _ => "album",
    };
}
