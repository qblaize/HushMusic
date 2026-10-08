using HushMusic.Playback.YtDlp;
using Xunit;

namespace HushMusic.Playback.Tests;

public sealed class DenoInstallerTests : IDisposable
{
    private const string Asset = "deno-x86_64-pc-windows-msvc.zip";
    private const string Hash = "a0c3101b4158d1dfb7d6a78a7bf0f3de80c96bb423c152beec8beb22786f2238";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "HushMusic.Playback.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void ParseChecksum_reads_the_powershell_listing_deno_publishes_for_windows()
    {
        // deno-x86_64-pc-windows-msvc.zip.sha256sum of v2.9.7, as served (CRLF, uppercase hex).
        const string listing = "\r\nAlgorithm : SHA256\r\nHash      : A0C3101B4158D1DFB7D6A78A7BF0F3DE80C96BB423C152BEEC8BEB22786F2238\r\n"
            + "Path      : C:\\a\\deno\\deno\\target\\release\\deno-x86_64-pc-windows-msvc.zip\r\n\r\n";

        Assert.Equal(Hash, DenoInstaller.ParseChecksum(listing, Asset));
    }

    [Theory]
    [InlineData(Hash + "  " + Asset + "\n")]
    [InlineData(Hash + " *" + Asset + "\n")]
    [InlineData(Hash + "  ./target/release/" + Asset + "\n")]
    [InlineData(Hash + "\n")]
    public void ParseChecksum_reads_sha256sum_lines(string listing) =>
        Assert.Equal(Hash, DenoInstaller.ParseChecksum(listing, Asset));

    [Theory]
    [InlineData("Algorithm : SHA256\nHash : " + Hash + "\nPath : C:\\release\\deno-aarch64-pc-windows-msvc.zip\n")]
    [InlineData("Algorithm : SHA512\nHash : " + Hash + "\nPath : C:\\release\\" + Asset + "\n")]
    [InlineData("Hash : 1234\n")]
    [InlineData(Hash + "  deno-aarch64-pc-windows-msvc.zip\n")]
    [InlineData("<!DOCTYPE html><html>Not Found</html>")]
    [InlineData("")]
    public void ParseChecksum_rejects_listings_for_other_files_or_without_a_sha256(string listing) =>
        Assert.Null(DenoInstaller.ParseChecksum(listing, Asset));

    [Theory]
    [InlineData("v2.9.7", "2.9.7")]
    [InlineData("v2.10.0", "2.10.0")]
    public void TryParseTag_accepts_release_tags(string tag, string expected)
    {
        Assert.True(DenoInstaller.TryParseTag(tag, out var version));
        Assert.Equal(Version.Parse(expected), version);
    }

    [Theory]
    [InlineData("2.9.7")]
    [InlineData("v2.0.0-rc.1")]
    [InlineData("v2.9")]
    [InlineData("v2.9.7.1")]
    [InlineData("latest")]
    [InlineData("v")]
    public void TryParseTag_rejects_everything_else(string tag) =>
        Assert.False(DenoInstaller.TryParseTag(tag, out _));

    [Fact]
    public void IsNewer_compares_versions_numerically()
    {
        Assert.True(DenoInstaller.IsNewer(new Version(2, 10, 0), new Version(2, 9, 7)));
        Assert.False(DenoInstaller.IsNewer(new Version(2, 9, 7), new Version(2, 9, 7)));
        Assert.False(DenoInstaller.IsNewer(new Version(2, 9, 6), new Version(2, 9, 7)));
        Assert.True(DenoInstaller.IsNewer(new Version(2, 9, 7), null));
    }

    [Fact]
    public void Update_checks_happen_weekly()
    {
        var now = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

        Assert.True(DenoInstaller.IsUpdateCheckDue(null, now));
        Assert.False(DenoInstaller.IsUpdateCheckDue(now.AddDays(-6.9), now));
        Assert.True(DenoInstaller.IsUpdateCheckDue(now.AddDays(-7), now));
        Assert.True(DenoInstaller.IsUpdateCheckDue(now.AddDays(1), now)); // clock went backwards
    }

    [Fact]
    public void The_last_check_survives_a_round_trip_and_missing_means_never()
    {
        Assert.Null(DenoInstaller.ReadLastCheck(_root));

        var when = new DateTimeOffset(2026, 10, 8, 10, 30, 0, TimeSpan.Zero);
        DenoInstaller.WriteLastCheck(_root, when);

        Assert.Equal(when, DenoInstaller.ReadLastCheck(_root));
    }

    [Fact]
    public void FindInstalled_picks_the_newest_complete_supported_version()
    {
        Install("2.9.7");
        Install("2.10.1");
        Install("2.2.0"); // older than yt-dlp's minimum
        Install("2.11.0.delete"); // being removed
        Install("2.12.0.extracting"); // interrupted install
        Directory.CreateDirectory(Path.Combine(_root, "2.13.0")); // no deno.exe

        var found = DenoInstaller.FindInstalled(_root);

        Assert.NotNull(found);
        Assert.Equal(new Version(2, 10, 1), found.Version);
        Assert.Equal(Path.Combine(_root, "2.10.1", "deno.exe"), found.ExecutablePath);
    }

    [Fact]
    public void FindInstalled_returns_null_without_a_usable_install()
    {
        Assert.Null(DenoInstaller.FindInstalled(_root));

        Install("2.2.0");
        Assert.Null(DenoInstaller.FindInstalled(_root));
    }

    [Fact]
    public void The_minimum_matches_yt_dlp()
    {
        // yt-dlp's EJS wiki / DenoJsRuntime.MIN_SUPPORTED_VERSION. Raise both together.
        Assert.Equal(new Version(2, 3, 0), DenoInstaller.MinimumVersion);
    }

    private void Install(string folder)
    {
        var dir = Path.Combine(_root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "deno.exe"), string.Empty);
    }
}
