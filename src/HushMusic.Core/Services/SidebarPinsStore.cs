using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Services;

/// <summary>
/// <see cref="ISidebarPins"/> in <c>sidebar-pins.json</c> under the app folder. Writes go to a temp file that replaces the
/// old one. Read in the background at startup. Pinned playlists may be the account's own (private ones too), so they
/// go when the user signs out or switches account; albums, artists and stations stay.
/// </summary>
public sealed class SidebarPinsStore(IAppPaths paths, IAuthService auth, ILogger<SidebarPinsStore> logger) : ISidebarPins, IHostedService
{
    public const string FileName = "sidebar-pins.json";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IgnoreReadOnlyProperties = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private List<SidebarPin> _items = [];
    private Task? _load;
    private Task _forget = Task.CompletedTask;
    private AuthStatus _authStatus;

    public event EventHandler? Changed;

    public IReadOnlyList<SidebarPin> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    private string FilePath => Path.Combine(paths.Root, FileName);

    public bool Contains(PinKind kind, string id)
    {
        lock (_gate)
        {
            return _items.Exists(p => p.Is(kind, id));
        }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _authStatus = auth.Status;
        }

        auth.StatusChanged += OnAuthStatusChanged;
        _ = LoadAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        auth.StatusChanged -= OnAuthStatusChanged;
        return Task.CompletedTask;
    }

    /// <summary>Completes when the pinned playlists of an account that signed out or switched are gone (tests).</summary>
    internal Task WhenForgottenAsync()
    {
        lock (_gate)
        {
            return _forget;
        }
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

    public async Task<bool> PinAsync(SidebarPin pin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pin);
        if (Normalize(pin) is not { } normalized)
        {
            throw new ArgumentException("A pin needs an id and a title.", nameof(pin));
        }

        await LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_items.Exists(p => p.Is(normalized.Kind, normalized.Id)))
            {
                return true;
            }

            if (_items.Count >= ISidebarPins.MaxCount)
            {
                return false;
            }

            _items.Add(normalized);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task UnpinAsync(PinKind kind, string id, CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_items.RemoveAll(p => p.Is(kind, id)) == 0)
            {
                return;
            }
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveAsync(PinKind kind, string id, int offset, CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var from = _items.FindIndex(p => p.Is(kind, id));
            if (from < 0)
            {
                return;
            }

            var to = Math.Clamp(from + offset, 0, _items.Count - 1);
            if (to == from)
            {
                return;
            }

            var pin = _items[from];
            _items.RemoveAt(from);
            _items.Insert(to, pin);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RefreshAsync(SidebarPin pin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pin);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var index = _items.FindIndex(p => p.Is(pin.Kind, pin.Id));
            if (index < 0)
            {
                return;
            }

            var current = _items[index];

            // A lookup that found no art (or no title) doesn't wipe what the pin already has.
            var updated = current with
            {
                Title = string.IsNullOrWhiteSpace(pin.Title) ? current.Title : pin.Title.Trim(),
                ThumbnailUrl = string.IsNullOrWhiteSpace(pin.ThumbnailUrl) ? current.ThumbnailUrl : pin.ThumbnailUrl,
                Station = pin.Station ?? current.Station,
            };
            if (updated == current)
            {
                return;
            }

            _items[index] = updated;
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    // Signed out, or signed in over a session (possibly another account): the pinned playlists may be that account's.
    // Signed out → signed in is the session restored at startup, or a sign-in after a sign-out that already cleared them.
    private void OnAuthStatusChanged(object? sender, AuthStatusChangedEventArgs e)
    {
        lock (_gate)
        {
            var previous = _authStatus;
            _authStatus = e.Status;
            var forget = (e.Status == AuthStatus.SignedOut && previous != AuthStatus.SignedOut)
                || (e.Status == AuthStatus.SignedIn && previous is AuthStatus.SignedIn or AuthStatus.Expired);
            if (forget)
            {
                _forget = _forget.ContinueWith(_ => ForgetPlaylistsAsync(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }
        }
    }

    private async Task ForgetPlaylistsAsync()
    {
        try
        {
            await LoadAsync(CancellationToken.None).ConfigureAwait(false);
            int removed;
            lock (_gate)
            {
                removed = _items.RemoveAll(p => p.Kind == PinKind.Playlist);
            }

            if (removed > 0)
            {
                logger.LogInformation("Unpinned {Count} playlists of the account that signed out or switched", removed);
                await SaveAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not unpin the playlists of the previous account");
        }
    }

    // Stored ids are normalized (no "VL" on playlists) and titles trimmed; null when the pin can't be shown or opened.
    private static SidebarPin? Normalize(SidebarPin? pin)
    {
        if (pin is null || string.IsNullOrWhiteSpace(pin.Id) || string.IsNullOrWhiteSpace(pin.Title) || !Enum.IsDefined(pin.Kind))
        {
            return null;
        }

        if (pin.Kind == PinKind.Station && (pin.Station is null || string.IsNullOrWhiteSpace(pin.Station.StreamUrl)))
        {
            return null;
        }

        return pin with { Id = SidebarPin.NormalizeId(pin.Kind, pin.Id.Trim()), Title = pin.Title.Trim() };
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        Changed?.Invoke(this, EventArgs.Empty);
        await WriteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadAsync()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var file = await JsonSerializer.DeserializeAsync<PinsFile>(stream, JsonOptions).ConfigureAwait(false);
            var pins = (file?.Pins ?? [])
                .Select(Normalize)
                .OfType<SidebarPin>()
                .DistinctBy(p => (p.Kind, p.Id))
                .Take(ISidebarPins.MaxCount)
                .ToList();
            lock (_gate)
            {
                _items = pins;
            }

            logger.LogDebug("Loaded {Count} sidebar pins", pins.Count);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning(ex, "The sidebar pins file is unreadable; starting with none");
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read under the write gate, so the last write always carries the latest list.
            var pins = Items;
            var path = FilePath;
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, new PinsFile(FormatVersion, pins), JsonOptions, CancellationToken.None).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HushException("Couldn't save your pinned items.", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private sealed record PinsFile(int Version, IReadOnlyList<SidebarPin?>? Pins);
}

public static class SidebarPinsServiceCollectionExtensions
{
    /// <summary>Playlists, albums, artists and stations pinned to the sidebar.</summary>
    public static IServiceCollection AddSidebarPins(this IServiceCollection services)
    {
        services.AddSingleton<SidebarPinsStore>();
        services.AddSingleton<ISidebarPins>(sp => sp.GetRequiredService<SidebarPinsStore>());
        services.AddHostedService(sp => sp.GetRequiredService<SidebarPinsStore>());
        return services;
    }
}
