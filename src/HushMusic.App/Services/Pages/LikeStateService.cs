using System.Collections.Concurrent;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Services.Pages;

/// <summary>
/// Current like state of tracks. Parsed models are immutable snapshots, so ratings made in this session
/// (from any part of the app) are kept here and override the parsed <see cref="Track.LikeStatus"/>.
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

    public LikeStateService(IAccountActionsService account, IAuthService auth, IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        account.TrackRated += OnTrackRated;
        auth.StatusChanged += OnAuthStatusChanged;
    }

    public event EventHandler<TrackRatedEventArgs>? Changed;

    public LikeStatus GetStatus(Track track) =>
        _overrides.TryGetValue(track.VideoId, out var status) ? status : track.LikeStatus ?? LikeStatus.Indifferent;

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
