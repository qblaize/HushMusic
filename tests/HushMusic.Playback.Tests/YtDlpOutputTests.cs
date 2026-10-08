using HushMusic.Playback.YtDlp;
using Xunit;

namespace HushMusic.Playback.Tests;

public sealed class YtDlpOutputTests
{
    [Theory]
    [InlineData("WARNING: [youtube] No supported JavaScript runtime could be found. Only deno is enabled by default; to use another runtime add  --js-runtimes RUNTIME[:PATH]  to your command/config.")]
    [InlineData("WARNING: [youtube] dQw4w9WgXcQ: n challenge solving failed: Some formats may be missing. Ensure you have a supported JavaScript runtime and challenge solver script distribution installed.")]
    [InlineData("WARNING: [youtube] dQw4w9WgXcQ: Signature solving failed: Some formats may be missing.")]
    [InlineData("WARNING: [youtube] [jsc:deno] Error running deno process (returncode: 1): error: Uncaught SyntaxError")]
    public void FindJsRuntimeProblem_spots_challenge_solver_trouble(string line)
    {
        var stderr = "[youtube] Extracting URL: https://music.youtube.com/watch?v=dQw4w9WgXcQ\n" + line + "\n";

        Assert.Equal(line, YtDlpOutput.FindJsRuntimeProblem(stderr));
    }

    [Theory]
    [InlineData("")]
    [InlineData("WARNING: [youtube] dQw4w9WgXcQ: Some web client https formats have been skipped as they are missing a url.\n")]
    [InlineData("[youtube] [jsc:deno] Solving JS challenges using deno\n")]
    [InlineData("ERROR: [youtube] dQw4w9WgXcQ: Video unavailable\n")]
    public void FindJsRuntimeProblem_ignores_everything_else(string stderr) =>
        Assert.Null(YtDlpOutput.FindJsRuntimeProblem(stderr));

    [Fact]
    public void FindHash_reads_yt_dlp_checksums()
    {
        const string sums = "0f4d2f0c0e52a7c0b8a1d9e4f3b7a6c5d4e3f2a1b0c9d8e7f6a5b4c3d2e1f0a9  yt-dlp_win.zip\n"
            + "1f4d2f0c0e52a7c0b8a1d9e4f3b7a6c5d4e3f2a1b0c9d8e7f6a5b4c3d2e1f0a9  yt-dlp_win_arm64.zip\n";

        Assert.Equal("1f4d2f0c0e52a7c0b8a1d9e4f3b7a6c5d4e3f2a1b0c9d8e7f6a5b4c3d2e1f0a9", YtDlpInstaller.FindHash(sums, "yt-dlp_win_arm64.zip"));
        Assert.Null(YtDlpInstaller.FindHash(sums, "yt-dlp_win_x86.zip"));
    }
}
