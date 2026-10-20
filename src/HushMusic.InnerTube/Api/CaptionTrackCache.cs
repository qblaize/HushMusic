using HushMusic.InnerTube.Parsing;

namespace HushMusic.InnerTube.Api;

/// <summary>
/// Caption track lists of recently played videos, taken from the anonymous <c>player</c> response that
/// <see cref="WatchApi.GetLoudnessDbAsync"/> already fetches for every track, so captions need no second
/// <c>player</c> call. The signed track URLs expire, so entries are used for <see cref="Lifetime"/> only.
/// </summary>
internal sealed class CaptionTrackCache(TimeProvider timeProvider)
{
    public const int Capacity = 64;

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, IReadOnlyList<CaptionTrack> Tracks)> _tracks = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    public void Store(string videoId, IReadOnlyList<CaptionTrack> tracks)
    {
        lock (_gate)
        {
            if (!_tracks.ContainsKey(videoId))
            {
                _order.Enqueue(videoId);
                while (_order.Count > Capacity)
                {
                    _tracks.Remove(_order.Dequeue());
                }
            }

            _tracks[videoId] = (timeProvider.GetUtcNow(), tracks);
        }
    }

    public IReadOnlyList<CaptionTrack>? TryGet(string videoId)
    {
        lock (_gate)
        {
            return _tracks.TryGetValue(videoId, out var entry) && timeProvider.GetUtcNow() - entry.At < Lifetime ? entry.Tracks : null;
        }
    }

    public void Remove(string videoId)
    {
        lock (_gate)
        {
            _tracks.Remove(videoId);
        }
    }
}
