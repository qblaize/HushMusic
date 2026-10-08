using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Models;
using HushMusic.Core.Queue;
using Xunit;

namespace HushMusic.Playback.Tests;

/// <summary>
/// Smoke tests on the real player (two MediaPlayers, the mixer and the system media controls), without any network:
/// the stream resolver here never returns a playable link.
/// </summary>
public sealed class MediaPlayerServiceTests : IDisposable
{
    private readonly QueueService _queue = new();
    private readonly StubSettings _settings = new();
    private readonly FailingResolver _resolver = new();
    private readonly MediaPlayerService _player;
    private readonly ConcurrentQueue<string> _events = new();

    public MediaPlayerServiceTests()
    {
        var normalizer = new VolumeNormalizer(new SilentWatchApi(), _settings, NullLogger<VolumeNormalizer>.Instance);
        _player = new MediaPlayerService(_queue, _resolver, _settings, normalizer, new StubRadio(), NullLogger<MediaPlayerService>.Instance);
        _player.TrackChanged += (_, e) => _events.Enqueue("changed " + e.Track?.VideoId);
        _player.TrackStarted += (_, e) => _events.Enqueue("started " + e.Track?.VideoId);
        _player.TrackCompleted += (_, e) => _events.Enqueue("completed " + e.Track?.VideoId);
        _player.PlaybackFailed += (_, e) => _events.Enqueue("failed " + e.Track?.VideoId + ": " + e.Message);
    }

    public void Dispose() => _player.Dispose();

    [Fact]
    public void Starts_idle_with_the_saved_volume()
    {
        Assert.Equal(PlaybackStatus.Idle, _player.Status);
        Assert.Null(_player.CurrentTrack);
        Assert.Equal(0.8, _player.Volume, 10);
        Assert.Equal(TimeSpan.Zero, _player.Position);
    }

    [Fact]
    public async Task A_track_without_a_stream_fails_with_the_resolvers_message()
    {
        _queue.Load([Song("a"), Song("b")]);

        await _player.PlayAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => _events.Contains("failed a: No stream for a"));

        Assert.Equal(PlaybackStatus.Failed, _player.Status);
        Assert.Equal(["changed a", "failed a: No stream for a"], _events.ToArray());
        Assert.Equal(0, _queue.CurrentIndex); // a failed user choice does not skip ahead
    }

    [Fact]
    public async Task Settings_changes_and_controls_on_an_empty_player_are_harmless()
    {
        await _settings.UpdateAsync(s => s.CrossfadeSeconds = 6, TestContext.Current.CancellationToken);
        await _settings.UpdateAsync(s => s.NormalizeVolume = false, TestContext.Current.CancellationToken);
        _player.Pause();
        _player.Seek(TimeSpan.FromSeconds(10));
        _player.Stop();
        _player.IsMuted = true;
        _player.Volume = 0.3;

        Assert.Equal(PlaybackStatus.Idle, _player.Status);
        Assert.True(_player.IsMuted);
        Assert.Equal(0.3, _player.Volume, 10);
    }

    [Fact]
    public async Task Clearing_the_queue_while_loading_stops_cleanly()
    {
        _resolver.Hang = true;
        _queue.Load([Song("a"), Song("b")]);
        _ = _player.PlayAsync(TestContext.Current.CancellationToken);
        await WaitForAsync(() => _player.Status == PlaybackStatus.Loading);

        _queue.Clear();

        await WaitForAsync(() => _player.Status == PlaybackStatus.Idle);
        Assert.Null(_player.CurrentTrack);
        await WaitForAsync(() => _resolver.Cancelled > 0);
    }

    private static Track Song(string id) => new() { Title = "Song " + id, VideoId = id, Duration = TimeSpan.FromMinutes(3) };

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 5 s");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class FailingResolver : IStreamResolver
    {
        private int _cancelled;

        public bool Hang { get; set; }

        public int Cancelled => Volatile.Read(ref _cancelled);

        public async Task<ResolvedStream> ResolveAsync(string videoId, CancellationToken cancellationToken = default)
        {
            if (Hang)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _cancelled);
                    throw;
                }
            }

            throw new HushException("No stream for " + videoId);
        }

        public void Invalidate(string videoId)
        {
        }

        public void Prefetch(string videoId)
        {
        }

        public Task<string?> GetBackendVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task<bool> UpdateBackendAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    private sealed class StubSettings : ISettingsService
    {
        public AppSettings Current { get; } = new();

        public event EventHandler? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default)
        {
            update(Current);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class StubRadio : IRadioNowPlaying
    {
        public RadioNowPlaying? Current => null;

        public event EventHandler<RadioNowPlayingChangedEventArgs>? Changed { add { } remove { } }

        public void Follow(RadioStation station)
        {
        }

        public void Unfollow(bool forget)
        {
        }
    }

    private sealed class SilentWatchApi : IWatchApi
    {
        public Task<WatchPlaylist> GetWatchPlaylistAsync(string? videoId, string? playlistId = null, bool radio = false, bool shuffle = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Paged<Track>> GetWatchPlaylistContinuationAsync(string continuation, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Lyrics?> GetLyricsAsync(string lyricsBrowseId, bool timestamps = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Shelf>> GetRelatedAsync(string relatedBrowseId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<double?> GetLoudnessDbAsync(string videoId, CancellationToken cancellationToken = default) => Task.FromResult<double?>(null);
    }
}
