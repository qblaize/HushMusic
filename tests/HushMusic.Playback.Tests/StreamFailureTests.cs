using HushMusic.Core;
using HushMusic.Playback.YtDlp;
using Xunit;

namespace HushMusic.Playback.Tests;

/// <summary>How yt-dlp's errors are told apart, and what the user is told about an age-restricted track.</summary>
public sealed class StreamFailureTests
{
    private const string Extracting = "[youtube] Extracting URL: https://music.youtube.com/watch?v=Dkl76D3NKis\n"
        + "[youtube] Dkl76D3NKis: Downloading webpage\n"
        + "[youtube] Dkl76D3NKis: Downloading tv client config\n";

    [Theory]
    // yt-dlp 2026.10.26, anonymous, on an age-gated YouTube Music track (every player client answers the same).
    [InlineData("ERROR: [youtube] Dkl76D3NKis: Sign in to confirm your age. This video may be inappropriate for some users. Use --cookies-from-browser or --cookies for the authentication. See  https://github.com/yt-dlp/yt-dlp/wiki/FAQ#how-do-i-pass-cookies-to-yt-dlp  for how to manually pass cookies. Also see  https://github.com/yt-dlp/yt-dlp/wiki/Extractors#exporting-youtube-cookies  for tips on effectively exporting YouTube cookies")]
    [InlineData("ERROR: [youtube] Dkl76D3NKis: This video may be inappropriate for some users.")]
    [InlineData("ERROR: [youtube] Dkl76D3NKis: This video is age-restricted and only available on YouTube.")]
    public void Age_gate_errors_are_age_restricted(string error) =>
        Assert.Equal(StreamFailureReason.AgeRestricted, YtDlpOutput.ClassifyError(Extracting + error + "\n"));

    [Theory]
    // The bot check is about the connection, not the track: it must not be remembered for the track.
    [InlineData("ERROR: [youtube] Dkl76D3NKis: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.")]
    [InlineData("ERROR: [youtube] Dkl76D3NKis: Video unavailable. This content isn’t available, try again later.")]
    [InlineData("ERROR: [youtube] Dkl76D3NKis: Private video. Sign in if you've been granted access to this video")]
    [InlineData("ERROR: [youtube] Dkl76D3NKis: Video unavailable. This video is not available in your country")]
    [InlineData("ERROR: [youtube] Dkl76D3NKis: Requested format is not available. Use --list-formats for a list of available formats")]
    [InlineData("")]
    public void Other_errors_are_not(string error) =>
        Assert.Equal(StreamFailureReason.Other, YtDlpOutput.ClassifyError(Extracting + error + "\n"));

    [Fact]
    public void An_age_warning_before_a_different_error_does_not_count()
    {
        // Only the final error decides.
        const string stderr = "WARNING: [youtube] Dkl76D3NKis: Sign in to confirm your age (retrying with another client)\n"
            + "ERROR: [youtube] Dkl76D3NKis: Video unavailable\n";

        Assert.Equal(StreamFailureReason.Other, YtDlpOutput.ClassifyError(stderr));
    }

    [Fact]
    public void Signed_out_users_are_told_to_sign_in_and_turn_the_account_on()
    {
        var message = YtDlpStreamResolver.AgeRestrictedMessage(signedIn: false, usedAccount: false);

        Assert.StartsWith("This song is age-restricted on YouTube.", message, StringComparison.Ordinal);
        Assert.Contains("sign in", message, StringComparison.Ordinal);
        Assert.Contains("\"Use my account when resolving streams\" in Settings → Playback", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Signed_in_users_are_told_to_turn_the_account_on()
    {
        var message = YtDlpStreamResolver.AgeRestrictedMessage(signedIn: true, usedAccount: false);

        Assert.Equal(
            "This song is age-restricted on YouTube. To play it, turn on \"Use my account when resolving streams\" in Settings → Playback.",
            message);
    }

    [Fact]
    public void With_the_account_already_used_the_age_has_to_be_confirmed_on_youtube()
    {
        var message = YtDlpStreamResolver.AgeRestrictedMessage(signedIn: true, usedAccount: true);

        Assert.DoesNotContain("Use my account", message, StringComparison.Ordinal);
        Assert.Contains("Confirm your age on YouTube", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reason_travels_with_the_exception()
    {
        var plain = new StreamResolutionException("Dkl76D3NKis", "yt-dlp failed with exit code 1.");
        var restricted = new StreamResolutionException("Dkl76D3NKis", "age", reason: StreamFailureReason.AgeRestricted);

        Assert.Equal(StreamFailureReason.Other, plain.Reason);
        Assert.Equal(StreamFailureReason.AgeRestricted, restricted.Reason);
    }
}
