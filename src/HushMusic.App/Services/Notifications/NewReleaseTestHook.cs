using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Services.Notifications;

/// <summary>
/// Test instances only (HUSHMUSIC_TEST_BACKGROUND=1): HUSHMUSIC_TEST_NEW_RELEASES=list (three releases) or =album (one)
/// announces sample releases 4 s after startup, the way a real round would (the notification is only logged), then acts
/// as if it was clicked 2 s later. Test instances aren't signed in, so this is how the click targets can be seen.
/// </summary>
internal static class NewReleaseTestHook
{
    private const string DaftPunk = "UCRr1xG_2WIDs18a6cIiCxeA";

    public static void Start(TrackNotificationService notifications)
    {
        var mode = Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_NEW_RELEASES");
        if (Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_BACKGROUND") != "1" || mode is not ("list" or "album"))
        {
            return;
        }

        IReadOnlyList<NewRelease> releases =
        [
            Sample("MPREb_X1DQ1j0PPrX", "GLBTM (Studio Outtakes)", AlbumType.Single, "fgqj_3-DKrpBQNAoK9RmUAbW6-JaDbcPRF4ZsWjsLa1U-3m6X7pvg2QF5QrZLvs6HfTFS-d8DlKzYANK"),
            Sample("MPREb_OgeEnoHTsCm", "Random Access Memories (Drumless Edition)", AlbumType.Album, "wm1nkwlINP48XhoI_M_m5Z7IWIw742yukaiKKxqQGcskmB6TdcKZqTsBNChGW_I1BphPt7OkBwu9kRE"),
            Sample("MPREb_7VGCTgAVWt7", "The Writing of Fragments of Time", AlbumType.Single, "zeR-DsNueuj68BJ2ORGyBYBmQ4OcDew557-nkYwoR2yIE9LEZBh5R4I-yTTXoT-Qux5tVnsOIXvvK9w"),
        ];
        if (mode == "album")
        {
            releases = [releases[0]];
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            await notifications.ShowNewReleasesAsync(releases).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            notifications.OpenRelease(mode == "album" ? releases[0].Album.BrowseId : null);
        });
    }

    private static NewRelease Sample(string browseId, string title, AlbumType type, string art) => new(
        DaftPunk,
        "Daft Punk",
        new Album
        {
            Title = title,
            BrowseId = browseId,
            Type = type,
            Year = "2023",
            Thumbnails = [new Thumbnail($"https://yt3.googleusercontent.com/{art}=w544-h544-l90-rj", 544, 544)],
            Artists = [new ArtistRef("Daft Punk", DaftPunk)],
        });
}
