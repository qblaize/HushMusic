using Xunit;

namespace HushMusic.Playback.Tests;

public sealed class VideoSyncTests
{
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(500);

    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void Playing_in_step_runs_at_normal_speed()
    {
        var decision = VideoSync.Decide(audioPlaying: true, audio: Ms(10_000), video: Ms(9_975), videoPlaying: true, Lead);

        Assert.Equal(VideoSyncDecision.PlayAt(1), decision);
    }

    [Theory]
    [InlineData(200, 1.10)] // picture 200 ms behind: 10% faster
    [InlineData(-200, 0.90)]
    [InlineData(600, 1.12)] // at most 12%
    [InlineData(-900, 0.88)]
    public void Small_drift_is_closed_by_speed(double driftMs, double rate)
    {
        var decision = VideoSync.Decide(true, Ms(60_000), Ms(60_000 - driftMs), videoPlaying: true, Lead);

        Assert.Equal(VideoSyncAction.Play, decision.Action);
        Assert.Equal(rate, decision.Rate, precision: 6);
    }

    [Theory]
    [InlineData(1_500)]
    [InlineData(-1_500)]
    [InlineData(47_000)] // the sound was sought
    public void Large_drift_while_playing_seeks_ahead_of_the_sound(double driftMs)
    {
        var decision = VideoSync.Decide(true, Ms(60_000), Ms(60_000 - driftMs), videoPlaying: true, Lead);

        Assert.Equal(VideoSyncDecision.SeekTo(Ms(60_500)), decision);
    }

    [Fact]
    public void Picture_waiting_ahead_starts_when_the_sound_gets_there()
    {
        var decision = VideoSync.Decide(true, Ms(60_000), Ms(60_300), videoPlaying: false, Lead);

        Assert.Equal(VideoSyncDecision.HoldFor(Ms(280)), decision);
    }

    [Fact]
    public void Picture_waiting_and_reached_starts_playing()
    {
        Assert.Equal(VideoSyncDecision.PlayAt(1), VideoSync.Decide(true, Ms(60_000), Ms(60_010), videoPlaying: false, Lead));
        Assert.Equal(VideoSyncDecision.PlayAt(1), VideoSync.Decide(true, Ms(60_000), Ms(59_980), videoPlaying: false, Lead));
    }

    [Fact]
    public void Picture_paused_a_little_behind_catches_up_by_speed()
    {
        var decision = VideoSync.Decide(true, Ms(60_000), Ms(59_800), videoPlaying: false, Lead);

        Assert.Equal(VideoSyncAction.Play, decision.Action);
        Assert.Equal(1.10, decision.Rate, precision: 6);
    }

    [Fact]
    public void Picture_paused_far_behind_or_far_ahead_seeks_again()
    {
        Assert.Equal(VideoSyncDecision.SeekTo(Ms(60_500)), VideoSync.Decide(true, Ms(60_000), Ms(59_500), videoPlaying: false, Lead));
        Assert.Equal(VideoSyncDecision.SeekTo(Ms(10_500)), VideoSync.Decide(true, Ms(10_000), Ms(60_000), videoPlaying: false, Lead));
    }

    [Fact]
    public void Paused_sound_pauses_the_picture_on_the_same_frame()
    {
        Assert.Equal(VideoSyncDecision.Pause, VideoSync.Decide(false, Ms(9_568), Ms(9_534), videoPlaying: true, Lead));
        Assert.Equal(VideoSyncDecision.None, VideoSync.Decide(false, Ms(9_568), Ms(9_534), videoPlaying: false, Lead));
        Assert.Equal(VideoSyncDecision.SeekTo(Ms(30_000)), VideoSync.Decide(false, Ms(30_000), Ms(9_534), videoPlaying: false, Lead));
        Assert.Equal(VideoSyncDecision.SeekTo(Ms(30_000)), VideoSync.Decide(false, Ms(30_000), Ms(29_800), videoPlaying: true, Lead));
    }

    [Fact]
    public void Seek_never_aims_before_the_start()
    {
        Assert.Equal(TimeSpan.Zero, VideoSyncDecision.SeekTo(Ms(-200)).Target);
    }

    [Theory]
    [InlineData(500, 200, 425)]
    [InlineData(500, 3_000, 1_825)]
    [InlineData(2_500, 9_000, 3_000)] // at most 3 s
    [InlineData(250, 0, 250)] // at least 250 ms
    public void Lead_follows_how_long_seeks_take(double currentMs, double tookMs, double expectedMs) =>
        Assert.Equal(Ms(expectedMs), VideoSync.NextLead(Ms(currentMs), Ms(tookMs)));
}
