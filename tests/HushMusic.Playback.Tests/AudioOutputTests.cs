using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Models;
using HushMusic.Core.Queue;
using Xunit;

namespace HushMusic.Playback.Tests;

/// <summary>
/// The output choice on the real player and the real output list of the machine running the tests. Nothing plays:
/// the players have no media, so moving them between outputs is silent.
/// </summary>
public sealed class AudioOutputTests : IDisposable
{
    private const string NotConnected = @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{00000000-0000-0000-0000-000000000000}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    private readonly StubSettings _settings = new();
    private readonly CapturingLogger _log = new();
    private readonly MediaPlayerService _player;

    public AudioOutputTests()
    {
        var normalizer = new VolumeNormalizer(new SilentWatchApi(), _settings, NullLogger<VolumeNormalizer>.Instance);
        _player = new MediaPlayerService(new QueueService(), new NoResolver(), _settings, normalizer, new StubRadio(), _log);
    }

    public void Dispose() => _player.Dispose();

    [Fact]
    public async Task The_watcher_lists_the_outputs_and_names_them()
    {
        using var outputs = new AudioOutputWatcher(NullLogger.Instance);
        outputs.Start();
        await WaitForAsync(() => outputs.IsEnumerated);

        foreach (var device in outputs.Devices)
        {
            Assert.True(device.IsConnected);
            Assert.Equal(device.Name, outputs.NameOf(device.Id));
            Assert.Equal(device.Id, outputs.Find(device.Id)?.Id);
        }

        Assert.Null(outputs.Find(NotConnected));
        Assert.Null(await AudioOutputWatcher.FindNameAsync(NotConnected, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_chosen_output_that_is_not_connected_leaves_the_player_on_the_system_default()
    {
        await _settings.UpdateAsync(s => s.AudioOutputDeviceId = NotConnected, TestContext.Current.CancellationToken);

        await WaitForAsync(() => _log.Contains($"Audio output {NotConnected} isn't connected"));
        Assert.DoesNotContain(_log.Messages, m => m.StartsWith("Audio output: ", StringComparison.Ordinal));
        Assert.Equal(PlaybackStatus.Idle, _player.Status);
    }

    [Fact]
    public async Task A_connected_output_is_used_and_the_system_default_again_once_it_is_cleared()
    {
        using var outputs = new AudioOutputWatcher(NullLogger.Instance);
        outputs.Start();
        await WaitForAsync(() => outputs.IsEnumerated);
        if (outputs.Devices is not [var device, ..])
        {
            Assert.Skip("This machine has no audio output.");
            return;
        }

        await _settings.UpdateAsync(s => s.AudioOutputDeviceId = device.Id.ToUpperInvariant(), TestContext.Current.CancellationToken);
        await WaitForAsync(() => _log.Contains($"Audio output: {device.Name}"));

        await _settings.UpdateAsync(s => s.AudioOutputDeviceId = null, TestContext.Current.CancellationToken);
        await WaitForAsync(() => _log.Contains("Audio output: system default"));
        Assert.Equal(PlaybackStatus.Idle, _player.Status);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 5 s");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class CapturingLogger : ILogger<MediaPlayerService>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IEnumerable<string> Messages => _messages;

        public bool Contains(string start) => _messages.Any(m => m.StartsWith(start, StringComparison.Ordinal));

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                _messages.Enqueue(formatter(state, exception));
            }
        }
    }

    private sealed class NoResolver : IStreamResolver
    {
        public Task<ResolvedStream> ResolveAsync(string videoId, CancellationToken cancellationToken = default) =>
            throw new HushException("No stream for " + videoId);

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
