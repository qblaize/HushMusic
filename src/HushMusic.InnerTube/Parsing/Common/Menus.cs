using System.Text.Json.Nodes;
using HushMusic.Core.Models;

namespace HushMusic.InnerTube.Parsing.Common;

/// <summary>Per-item state read from context menus and like buttons (ytmusicapi parse_song_menu_data / parse_like_status).</summary>
internal static class Menus
{
    public static JsonArray? Items(JsonNode? item) => item.Arr("menu", "menuRenderer", "items");

    /// <summary>
    /// Library add/remove tokens from the "Save to library" toggle. The icon tells the current state:
    /// BOOKMARK_BORDER = not in library (default endpoint adds), BOOKMARK = in library (default endpoint removes).
    /// Signed out, the add endpoint is a sign-in modal, so only one token (or none) is present.
    /// </summary>
    public static FeedbackTokens? LibraryTokens(JsonNode? item)
    {
        FeedbackTokens? tokens = null;
        foreach (var entry in Items(item).Objects())
        {
            var menuItem = entry.Obj("toggleMenuServiceItemRenderer") ?? entry.Obj("menuServiceItemRenderer");
            if (menuItem is null)
            {
                continue;
            }

            var icon = menuItem.Str("defaultIcon", "iconType") ?? menuItem.Str("icon", "iconType");
            var defaultToken = menuItem.Str("defaultServiceEndpoint", "feedbackEndpoint", "feedbackToken");
            var toggledToken = menuItem.Str("toggledServiceEndpoint", "feedbackEndpoint", "feedbackToken");
            tokens = icon switch
            {
                "BOOKMARK_BORDER" => new FeedbackTokens(Add: defaultToken, Remove: toggledToken),
                "BOOKMARK" => new FeedbackTokens(Add: toggledToken, Remove: defaultToken),
                _ => tokens,
            };
        }

        return tokens is { Add: null, Remove: null } ? null : tokens;
    }

    /// <summary>Current like state from a <c>likeButtonRenderer.likeStatus</c> / <c>likeEndpoint.status</c> value.</summary>
    public static LikeStatus? ParseLikeStatus(string? value) => value switch
    {
        "LIKE" => LikeStatus.Like,
        "DISLIKE" => LikeStatus.Dislike,
        "INDIFFERENT" => LikeStatus.Indifferent,
        _ => null,
    };

    /// <summary>
    /// Like toggles carry the action they would perform, not the current state: a default action of
    /// LIKE means the item is not liked yet, INDIFFERENT (remove like) means it is liked.
    /// Signed out, the toggle opens a sign-in modal instead and the state is unknown (null).
    /// </summary>
    public static LikeStatus? ParseToggleLikeStatus(JsonNode? serviceEndpoint) =>
        serviceEndpoint.Str("likeEndpoint", "status") switch
        {
            "LIKE" => LikeStatus.Indifferent,
            "INDIFFERENT" => LikeStatus.Like,
            _ => null,
        };
}
