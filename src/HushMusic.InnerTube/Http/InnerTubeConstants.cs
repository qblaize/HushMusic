namespace HushMusic.InnerTube.Http;

/// <summary>Literal values from ytmusicapi (<c>constants.py</c>, <c>helpers.initialize_headers</c>).</summary>
internal static class InnerTubeConstants
{
    public const string Domain = "https://music.youtube.com";

    public const string BaseApi = Domain + "/youtubei/v1/";

    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:88.0) Gecko/20100101 Firefox/88.0";

    // "Consent rejected" cookie that ytmusicapi sends on every request (taken from yt-dlp).
    public const string ConsentCookie = "SOCS=CAI";

    public const string ClientName = "WEB_REMIX";

    // ytmusicapi YTMusicBase.as_mobile(), pinned there (unlike the date-based web version).
    public const string MobileClientName = "ANDROID_MUSIC";

    public const string MobileClientVersion = "7.21.50";

    // Parsers match English UI text, so the language is fixed.
    public const string Language = "en";
}
