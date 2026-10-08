using System.Text.Json.Nodes;

namespace HushMusic.InnerTube.Parsing.Common;

/// <summary>
/// Recognizes HTTP 200 responses that carry a sign-in prompt instead of account data (expired or missing
/// session; docs/innertube-requests.md 4.4 and 9.j). Captured signed out on 2026-10-07: history, liked songs
/// and every library tab return a single <c>itemSectionRenderer</c> holding a <c>messageRenderer</c>
/// ("Sign in to view your history") whose button opens a <c>signInEndpoint</c>; <c>account/account_menu</c>
/// returns the menu without its <c>activeAccountHeaderRenderer</c>.
/// </summary>
internal static class SignInDetector
{
    // Public pages viewed signed out also contain signInEndpoints, always behind a modalEndpoint (the
    // "Save to library"/"Like" buttons of rows and headers, the home taste-builder). A real prompt has the
    // sign-in button directly on a section-level renderer, so modals and item lists are not searched.
    private static readonly HashSet<string> SkippedKeys = ["contents", "items", "modalEndpoint"];

    public static bool LooksSignedOut(JsonNode response)
    {
        ArgumentNullException.ThrowIfNull(response);

        foreach (var section in TopLevelSections(response))
        {
            var (name, renderer) = section.Renderer();
            if (name == "itemSectionRenderer")
            {
                foreach (var item in renderer.Arr("contents").Objects())
                {
                    if (HasSignInEndpoint(item.Renderer().Value, depth: 0))
                    {
                        return true;
                    }
                }
            }
            else if (HasSignInEndpoint(renderer, depth: 0))
            {
                return true;
            }
        }

        var accountMenu = response.Obj("actions", 0, "openPopupAction", "popup", "multiPageMenuRenderer");
        return accountMenu.Str("style") == "MULTI_PAGE_MENU_STYLE_TYPE_ACCOUNT"
            && accountMenu.Obj("header", "activeAccountHeaderRenderer") is null;
    }

    private static IEnumerable<JsonObject> TopLevelSections(JsonNode response)
    {
        var contents = response.Obj("contents");
        foreach (var column in new[] { "singleColumnBrowseResultsRenderer", "twoColumnBrowseResultsRenderer" })
        {
            foreach (var tab in contents.Arr(column, "tabs").Objects())
            {
                foreach (var section in tab.Arr("tabRenderer", "content", "sectionListRenderer", "contents").Objects())
                {
                    yield return section;
                }
            }
        }

        foreach (var section in contents.Arr("twoColumnBrowseResultsRenderer", "secondaryContents", "sectionListRenderer", "contents").Objects())
        {
            yield return section;
        }

        foreach (var section in contents.Arr("sectionListRenderer", "contents").Objects())
        {
            yield return section;
        }
    }

    private static bool HasSignInEndpoint(JsonNode? node, int depth)
    {
        if (node is null || depth > 12)
        {
            return false;
        }

        if (node is JsonArray array)
        {
            return array.Any(child => HasSignInEndpoint(child, depth + 1));
        }

        if (node is not JsonObject obj)
        {
            return false;
        }

        foreach (var (key, child) in obj)
        {
            if (key == "signInEndpoint")
            {
                return true;
            }

            if (!SkippedKeys.Contains(key) && HasSignInEndpoint(child, depth + 1))
            {
                return true;
            }
        }

        return false;
    }
}
