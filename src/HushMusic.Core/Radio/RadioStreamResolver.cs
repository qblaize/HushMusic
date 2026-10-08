using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Radio;

/// <summary>
/// The app's <see cref="IStreamResolver"/>: live radio stations play their own stream URL directly; everything else goes
/// to <paramref name="inner"/> (yt-dlp). Radio ids ("radio:…") never reach the inner resolver, so they are never sent to
/// yt-dlp, prefetched, or written to its stream cache.
/// </summary>
public sealed class RadioStreamResolver(IStreamResolver inner) : IStreamResolver
{
    public Task<ResolvedStream> ResolveAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Station is not { } station)
        {
            return inner.ResolveAsync(track, cancellationToken);
        }

        if (!Uri.TryCreate(station.StreamUrl, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return Task.FromException<ResolvedStream>(new StreamResolutionException(track.VideoId, $"{station.Name} has no playable stream address."));
        }

        return Task.FromResult(new ResolvedStream
        {
            VideoId = track.VideoId,
            Url = url,
            Codec = station.Codec,
            BitrateKbps = station.BitrateKbps,
            ExpiresAt = DateTimeOffset.MaxValue,
            IsLive = true,
        });
    }

    public Task<ResolvedStream> ResolveAsync(string videoId, CancellationToken cancellationToken = default) =>
        LiveRadio.IsRadioId(videoId)
            ? Task.FromException<ResolvedStream>(new StreamResolutionException(videoId, "A radio station can only be resolved from its queue item."))
            : inner.ResolveAsync(videoId, cancellationToken);

    public void Invalidate(string videoId)
    {
        if (!LiveRadio.IsRadioId(videoId))
        {
            inner.Invalidate(videoId);
        }
    }

    public void Prefetch(string videoId)
    {
        if (!LiveRadio.IsRadioId(videoId))
        {
            inner.Prefetch(videoId);
        }
    }

    public Task<string?> GetBackendVersionAsync(CancellationToken cancellationToken = default) => inner.GetBackendVersionAsync(cancellationToken);

    public Task<bool> UpdateBackendAsync(CancellationToken cancellationToken = default) => inner.UpdateBackendAsync(cancellationToken);
}
