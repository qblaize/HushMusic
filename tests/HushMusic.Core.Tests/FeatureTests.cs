using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Models;
using HushMusic.Core.Queue;
using Xunit;
using static HushMusic.Core.Tests.TestData;

namespace HushMusic.Core.Tests;

public sealed class QueueAutoExtenderTests : IAsyncLifetime
{
    private readonly QueueService _queue = new();
    private readonly FakeWatchApi _watch = new();
    private readonly QueueAutoExtender _extender;

    public QueueAutoExtenderTests()
    {
        _extender = new QueueAutoExtender(_queue, _watch, NullLogger<QueueAutoExtender>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _extender.StartAsync(Ct);

    public async ValueTask DisposeAsync()
    {
        await _extender.StopAsync(CancellationToken.None);
        _extender.Dispose();
    }

    [Fact]
    public void Extends_when_few_items_remain_and_chains_until_enough_are_queued()
    {
        _watch.OnContinuation = (token, _) => Task.FromResult(token switch
        {
            "t1" => new Paged<Track>(Tracks("c", "d"), "t2"),
            "t2" => new Paged<Track>(Tracks("e", "f", "g"), "t3"),
            _ => throw new InvalidOperationException("should not be requested"),
        });

        _queue.Load(Tracks("a", "b"), continuation: "t1");

        Assert.Equal(["t1", "t2"], _watch.ContinuationCalls);
        Assert.Equal(["a", "b", "c", "d", "e", "f", "g"], Ids(_queue.Items));
        Assert.Equal("t3", _queue.Continuation);
    }

    [Fact]
    public void Does_nothing_while_more_than_three_items_remain()
    {
        _queue.Load(Tracks("a", "b", "c", "d", "e"), continuation: "t1");

        Assert.Empty(_watch.ContinuationCalls);

        _queue.MoveNext(userInitiated: true);
        _queue.MoveNext(userInitiated: true);

        Assert.Equal(["t1"], _watch.ContinuationCalls);
    }

    [Fact]
    public void Does_nothing_without_a_continuation()
    {
        _queue.Load(Tracks("a"));
        _queue.Enqueue(Tracks("b"));

        Assert.Empty(_watch.ContinuationCalls);
    }

    [Fact]
    public async Task Keeps_one_request_in_flight()
    {
        var page = new TaskCompletionSource<Paged<Track>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch.OnContinuation = (_, _) => page.Task;

        _queue.Load(Tracks("a", "b"), continuation: "t1");
        _queue.Enqueue(Tracks("c"));
        _queue.MoveNext(userInitiated: true);

        Assert.Single(_watch.ContinuationCalls);

        page.SetResult(new Paged<Track>(Tracks("d", "e", "f", "g"), null));
        await WaitUntilAsync(() => _queue.Items.Count == 7);
        Assert.Equal(["a", "b", "c", "d", "e", "f", "g"], Ids(_queue.Items));
    }

    [Fact]
    public async Task Drops_a_page_when_the_queue_was_replaced_meanwhile()
    {
        var page = new TaskCompletionSource<Paged<Track>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch.OnContinuation = (_, _) => page.Task;

        _queue.Load(Tracks("a"), continuation: "t1");
        _queue.Load(Tracks("x", "y", "z", "w", "v"));
        page.SetResult(new Paged<Track>(Tracks("b"), "t2"));
        await Task.Delay(50, Ct);

        Assert.Equal(["x", "y", "z", "w", "v"], Ids(_queue.Items));
        Assert.Null(_queue.Continuation);
    }

    [Fact]
    public void Does_not_retry_a_failed_token_immediately()
    {
        _watch.OnContinuation = (_, _) => Task.FromException<Paged<Track>>(new InnerTubeException("next", "boom"));

        _queue.Load(Tracks("a", "b"), continuation: "t1");
        _queue.Enqueue(Tracks("c"));
        _queue.MoveNext(userInitiated: true);

        Assert.Equal(["t1"], _watch.ContinuationCalls);
    }

    [Fact]
    public async Task Stops_listening_after_StopAsync()
    {
        await _extender.StopAsync(Ct);

        _queue.Load(Tracks("a"), continuation: "t1");

        Assert.Empty(_watch.ContinuationCalls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
    }
}

public sealed class PlayHistoryReporterTests
{
    private readonly FakePlayer _player = new();
    private readonly FakeAccountApi _account = new();
    private readonly FakeAuth _auth = new();
    private readonly FakeSettings _settings = new();
    private readonly PlayHistoryReporter _reporter;

    public PlayHistoryReporterTests()
    {
        _reporter = new PlayHistoryReporter(_player, _account, _auth, _settings, NullLogger<PlayHistoryReporter>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reports_started_tracks_when_signed_in()
    {
        await _reporter.StartAsync(Ct);

        _player.RaiseTrackStarted(Track("a"));
        _player.RaiseTrackStarted(Track("b"));

        Assert.Equal(["a", "b"], _account.HistoryItems);
    }

    [Theory]
    [InlineData(AuthStatus.SignedOut, true)]
    [InlineData(AuthStatus.Expired, true)]
    [InlineData(AuthStatus.SignedIn, false)]
    public async Task Skips_reporting_when_signed_out_or_disabled(AuthStatus status, bool reportingEnabled)
    {
        _auth.Status = status;
        _settings.Current.ReportPlaybackHistory = reportingEnabled;
        await _reporter.StartAsync(Ct);

        _player.RaiseTrackStarted(Track("a"));

        Assert.Empty(_account.HistoryItems);
    }

    [Fact]
    public async Task Swallows_failures()
    {
        _account.OnAddHistory = _ => Task.FromException(new InnerTubeException("player", "boom"));
        await _reporter.StartAsync(Ct);

        _player.RaiseTrackStarted(Track("a"));

        Assert.Equal(["a"], _account.HistoryItems);
    }

    [Fact]
    public async Task Stops_reporting_after_StopAsync()
    {
        await _reporter.StartAsync(Ct);
        await _reporter.StopAsync(Ct);

        _player.RaiseTrackStarted(Track("a"));

        Assert.Empty(_account.HistoryItems);
    }
}
