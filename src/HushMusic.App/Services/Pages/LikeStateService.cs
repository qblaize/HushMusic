using System.Collections.Concurrent;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Services.Pages;

/// <summary>
/// Current like state of tracks. Parsed models are immutable snapshots, so ratings made in this session
/// (from any part of the app) are kept here and override the parsed <see cref="Track.LikeStatus"/>. Songs disliked in an
/// earlier session count as disliked when the parsed state doesn't say liked (queues and search results often carry no
/// state, and watch queues never tell a dislike).
/// </summary>
public interface ILikeStateService
{
    /// <summary>Raised on the UI thread after a track was rated.</summary>
    event EventHandler<TrackRatedEventArgs>? Changed;

    LikeStatus GetStatus(Track track);
}

internal sealed class LikeStateService : ILikeStateService
{
    private readonly ConcurrentDictionary<string, LikeStatus> _overrides = new(StringComparer.Ordinal);
    private readonly IUiDispatcher _dispatcher;
    private readonly IBlocklist _blocklist;

    public LikeStateService(IAccountActionsService account, IAuthService auth, IUiDispatcher dispatcher, IBlocklist blocklist)
    {
        _dispatcher = dispatcher;
        _blocklist = blocklist;
        account.TrackRated += OnTrackRated;
        auth.StatusChanged += OnAuthStatusChanged;
    }

    public event EventHandler<TrackRatedEventArgs>? Changed;

    public LikeStatus GetStatus(Track track)
    {
        if (_overrides.TryGetValue(track.VideoId, out var status))
        {
            return status;
        }

        return track.LikeStatus is not LikeStatus.Like && _blocklist.IsDisliked(track.VideoId)
            ? LikeStatus.Dislike
            : track.LikeStatus ?? LikeStatus.Indifferent;
    }

    private void OnTrackRated(object? sender, TrackRatedEventArgs e)
    {
        _overrides[e.VideoId] = e.Status;
        _dispatcher.Run(() => Changed?.Invoke(this, e));
    }

    private void OnAuthStatusChanged(object? sender, AuthStatusChangedEventArgs e)
    {
        if (e.Status != AuthStatus.SignedIn)
        {
            _overrides.Clear();
        }
    }
}
