using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.NowPlaying;

/// <summary>
/// Lyrics and "Related" browse ids of a track. Both come from the watch ("next") response, which is also where
/// ytmusicapi's get_lyrics / get_song_related start from; the lyrics and related tabs share one call per track.
/// </summary>
public sealed class TrackBrowseIds(IWatchApi watch)
{
    private const int CacheSize = 30;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Task<WatchPlaylist>> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <summary>The shared request is not cancelled with <paramref name="cancellationToken"/>; only the wait is.</summary>
    public Task<WatchPlaylist> GetAsync(string videoId, CancellationToken cancellationToken)
    {
        Task<WatchPlaylist> task;
        lock (_gate)
        {
            if (!_cache.TryGetValue(videoId, out task!) || task.IsFaulted || task.IsCanceled)
            {
                task = watch.GetWatchPlaylistAsync(videoId, cancellationToken: CancellationToken.None);
                if (_cache.TryAdd(videoId, task))
                {
                    _order.Enqueue(videoId);
                    while (_order.Count > CacheSize)
                    {
                        _cache.Remove(_order.Dequeue());
                    }
                }
                else
                {
                    _cache[videoId] = task;
                }
            }
        }

        return task.WaitAsync(cancellationToken);
    }
}
