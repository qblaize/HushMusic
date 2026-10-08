using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Parsing;

/// <summary>"Signed in as", <c>account/account_menu {}</c> (ytmusicapi get_account_info).</summary>
internal static class AccountParser
{
    private const string Page = "Account";

    /// <summary>Returns null when the menu has no active account (signed out).</summary>
    public static AccountInfo? ParseAccountInfo(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);

        JsonObject? header = null;
        foreach (var action in response.Arr("actions").Objects())
        {
            header ??= action.Obj("openPopupAction", "popup", "multiPageMenuRenderer", "header", "activeAccountHeaderRenderer");
        }

        if (header is null)
        {
            scope.IgnoreItem("multiPageMenuRenderer", "no activeAccountHeaderRenderer (signed out)");
            return null;
        }

        if (TextRuns.Text(header.Obj("accountName")) is not { Length: > 0 } name)
        {
            scope.MissingStructure("activeAccountHeaderRenderer.accountName");
            return null;
        }

        var photo = Thumbnail.Largest(Thumbnails.From(header.Nav("accountPhoto", "thumbnails")));
        return new AccountInfo(name, TextRuns.Text(header.Obj("channelHandle")), photo?.Url);
    }
}
