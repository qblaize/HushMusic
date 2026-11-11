using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features;

/// <summary>
/// <see cref="IBlocklist"/> in <c>blocklist.json</c> under the app folder. Nothing runs between changes: a rating event,
/// a block or an unblock updates the lists, swaps in new lookup sets and rewrites the file (a temp file that replaces the
/// old one) in the background. The file is read in the background at startup; changes wait for it.
/// </summary>
/// <remarks>
/// Disliked songs belong to the signed-in account: they are forgotten on sign-out and when a sign-in replaces a session
/// (signed in or expired), which may be another account. Restoring the stored session at startup keeps them. Blocked
/// artists are a preference of this PC and stay.
/// </remarks>
public sealed partial class BlocklistService : IBlocklist, IHostedService
{
    public const string FileName = "blocklist.json";

    /// <summary>Disliked songs kept at most; past that the ones disliked longest ago are dropped.</summary>
    public const int MaxDislikedSongs = 10_000;

    private const int FormatVersion = 1;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IAccountActionsService _account;
    private readonly IAuthService _auth;
    private readonly IAppPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<BlocklistService> _logger;
    private readonly Lock _gate = new();

    // Guarded by _gate. Disliked songs in the order they were disliked; blocked artists most recent first.
    private readonly List<string> _dislikedOrder = [];
    private readonly List<BlockedArtist> _artists = [];
    private AuthStatus _authStatus;
    private Task? _load;
    private bool _saveRequested;
    private bool _saving;
    private Task _save = Task.CompletedTask;

    // Swapped whole on every change, so the queue fillers read them without a lock.
    private volatile IReadOnlySet<string> _disliked = new HashSet<string>(StringComparer.Ordinal);
    private volatile ArtistMatcher _matcher = ArtistMatcher.Empty;
    private volatile IReadOnlyList<BlockedArtist> _blocked = [];

    public BlocklistService(IAccountActionsService account, IAuthService auth, IAppPaths paths, TimeProvider time, ILogger<BlocklistService> logger)
    {
        _account = account;
        _auth = auth;
        _paths = paths;
        _time = time;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public IReadOnlyList<BlockedArtist> BlockedArtists => _blocked;

    private string FilePath => Path.Combine(_paths.Root, FileName);

    public bool IsDisliked(string videoId) => _disliked.Contains(videoId);

    public bool IsBlocked(ArtistRef artist) => _matcher.IsBlocked(artist);

    public bool IsLeftOut(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.LikeStatus == LikeStatus.Dislike || _disliked.Contains(track.VideoId) || _matcher.IsBlocked(track);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _authStatus = _auth.Status;
        }

        _account.TrackRated += OnTrackRated;
        _auth.StatusChanged += OnAuthStatusChanged;
        _ = LoadAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _account.TrackRated -= OnTrackRated;
        _auth.StatusChanged -= OnAuthStatusChanged;
        Task save;
        lock (_gate)
        {
            save = _save;
        }

        // The app is closing: give the last change a moment to reach the disk.
        await Task.WhenAny(save, Task.Delay(StopTimeout, _time, cancellationToken)).ConfigureAwait(false);
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_gate)
        {
            load = _load ??= Task.Run(ReadAsync, CancellationToken.None);
        }

        return load.WaitAsync(cancellationToken);
    }

    public async Task<bool> BlockAsync(ArtistRef artist, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artist);
        ArgumentException.ThrowIfNullOrWhiteSpace(artist.Name);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_matcher.IsBlocked(artist))
            {
                return false;
            }

            var id = string.IsNullOrWhiteSpace(artist.BrowseId) ? null : artist.BrowseId;
            _artists.Insert(0, new BlockedArtist(artist.Name.Trim(), id, _time.GetUtcNow()));
            PublishNoLock();
            RequestSaveNoLock();
        }

        LogBlocked(_logger, artist.BrowseId ?? artist.Name);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public async Task<bool> UnblockAsync(ArtistRef artist, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artist);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_artists.RemoveAll(entry => ArtistMatcher.Matches(entry, artist)) == 0)
            {
                return false;
            }

            PublishNoLock();
            RequestSaveNoLock();
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    // A dislike made anywhere in Hush adds the song; a like or a removed dislike takes it out.
    private void OnTrackRated(object? sender, TrackRatedEventArgs e) => _ = SetDislikedAsync(e.VideoId, e.Status == LikeStatus.Dislike);

    private async Task SetDislikedAsync(string videoId, bool disliked)
    {
        try
        {
            await LoadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the blocklist before a rating");
            return;
        }

        lock (_gate)
        {
            if (disliked == _disliked.Contains(videoId))
            {
                return;
            }

            if (disliked)
            {
                _dislikedOrder.Add(videoId);
                TrimNoLock();
            }
            else
            {
                _dislikedOrder.Remove(videoId);
            }

            PublishNoLock();
            RequestSaveNoLock();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    // Signed out, or signed in over a session (possibly another account): the disliked songs were that account's.
    // Signed out → signed in is a restore at startup or a sign-in after a sign-out, which already cleared them.
    private void OnAuthStatusChanged(object? sender, AuthStatusChangedEventArgs e)
    {
        bool clear;
        lock (_gate)
        {
            var previous = _authStatus;
            _authStatus = e.Status;
            clear = e.Status == AuthStatus.SignedOut
                || (e.Status == AuthStatus.SignedIn && previous is AuthStatus.SignedIn or AuthStatus.Expired);
        }

        if (clear)
        {
            _ = ClearDislikedAsync();
        }
    }

    private async Task ClearDislikedAsync()
    {
        try
        {
            await LoadAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the blocklist before forgetting the disliked songs");
            return;
        }

        int count;
        lock (_gate)
        {
            count = _dislikedOrder.Count;
            if (count == 0)
            {
                return;
            }

            _dislikedOrder.Clear();
            PublishNoLock();
            RequestSaveNoLock();
        }

        LogDislikesCleared(_logger, count);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void TrimNoLock()
    {
        var excess = _dislikedOrder.Count - MaxDislikedSongs;
        if (excess > 0)
        {
            _dislikedOrder.RemoveRange(0, excess);
        }
    }

    private void PublishNoLock()
    {
        _disliked = _dislikedOrder.ToHashSet(StringComparer.Ordinal);
        _blocked = [.. _artists];
        _matcher = new ArtistMatcher(_artists);
    }

    private async Task ReadAsync()
    {
        BlocklistFile? file = null;
        try
        {
            if (File.Exists(FilePath))
            {
                await using var stream = File.OpenRead(FilePath);
                file = await JsonSerializer.DeserializeAsync<BlocklistFile>(stream, JsonOptions).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "The blocklist file is unreadable; starting with an empty one");
        }

        if (file is null)
        {
            return;
        }

        lock (_gate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            _dislikedOrder.AddRange(file.DislikedSongs?.Where(id => !string.IsNullOrWhiteSpace(id) && seen.Add(id)) ?? []);
            TrimNoLock();
            _artists.AddRange(file.BlockedArtists?.Where(a => a is not null && !string.IsNullOrWhiteSpace(a.Name)) ?? []);
            PublishNoLock();
        }

        LogLoaded(_logger, _disliked.Count, _blocked.Count);
        if (_disliked.Count > 0 || _blocked.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Changes that come in while a write runs are written once more right after it.
    private void RequestSaveNoLock()
    {
        _saveRequested = true;
        if (!_saving)
        {
            _saving = true;
            _save = Task.Run(SaveAsync);
        }
    }

    private async Task SaveAsync()
    {
        while (true)
        {
            BlocklistFile file;
            lock (_gate)
            {
                if (!_saveRequested)
                {
                    _saving = false;
                    return;
                }

                _saveRequested = false;
                file = new BlocklistFile(FormatVersion, [.. _dislikedOrder], [.. _artists]);
            }

            try
            {
                var temp = FilePath + ".tmp";
                await using (var stream = File.Create(temp))
                {
                    await JsonSerializer.SerializeAsync(stream, file, JsonOptions).ConfigureAwait(false);
                }

                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not save the blocklist");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Loaded {Songs} disliked songs and {Artists} blocked artists")]
    private static partial void LogLoaded(ILogger logger, int songs, int artists);

    [LoggerMessage(Level = LogLevel.Information, Message = "Blocked the artist {Artist}")]
    private static partial void LogBlocked(ILogger logger, string artist);

    [LoggerMessage(Level = LogLevel.Information, Message = "Forgot {Count} disliked songs: the account signed out or changed")]
    private static partial void LogDislikesCleared(ILogger logger, int count);

    private sealed record BlocklistFile(int Version, IReadOnlyList<string>? DislikedSongs, IReadOnlyList<BlockedArtist>? BlockedArtists);
}
