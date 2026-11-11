using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class SidebarPinsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hush-pins-tests-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Pins_in_order_and_survive_a_restart()
    {
        var store = Create();
        var changes = 0;
        store.Changed += (_, _) => changes++;
        await store.LoadAsync(Ct);

        Assert.True(await store.PinAsync(Pin(PinKind.Playlist, "VLPL1", "Road trip"), Ct));
        Assert.True(await store.PinAsync(Pin(PinKind.Album, "MPREb_1", "Album"), Ct));
        Assert.True(await store.PinAsync(SidebarPin.For(Station("s1"))!, Ct));
        Assert.True(await store.PinAsync(Pin(PinKind.Playlist, "PL1", "Duplicate"), Ct)); // already there: stays put

        Assert.Equal(["PL1", "MPREb_1", "s1"], store.Items.Select(p => p.Id));
        Assert.Equal("Road trip", store.Items[0].Title);
        Assert.True(store.Contains(PinKind.Playlist, "VLPL1"));
        Assert.False(store.Contains(PinKind.Artist, "PL1"));
        Assert.Equal(3, changes);

        var reloaded = Create();
        await reloaded.LoadAsync(Ct);
        Assert.Equal(store.Items.Select(p => (p.Kind, p.Id, p.Title)), reloaded.Items.Select(p => (p.Kind, p.Id, p.Title)));
        Assert.Equal("https://example.com/s1.png", reloaded.Items[2].ThumbnailUrl);
        Assert.Equal("http://example.com/s1", reloaded.Items[2].Station?.StreamUrl);
        Assert.False(File.Exists(Path.Combine(_root, SidebarPinsStore.FileName + ".tmp")));
    }

    [Fact]
    public async Task Moves_and_unpins()
    {
        var store = Create();
        foreach (var id in new[] { "a", "b", "c" })
        {
            await store.PinAsync(Pin(PinKind.Artist, id, id.ToUpperInvariant()), Ct);
        }

        await store.MoveAsync(PinKind.Artist, "c", -1, Ct);
        Assert.Equal(["a", "c", "b"], store.Items.Select(p => p.Id));

        await store.MoveAsync(PinKind.Artist, "a", -1, Ct); // already first
        await store.MoveAsync(PinKind.Artist, "a", 10, Ct); // clamped to the end
        Assert.Equal(["c", "b", "a"], store.Items.Select(p => p.Id));

        await store.UnpinAsync(PinKind.Artist, "b", Ct);
        await store.UnpinAsync(PinKind.Artist, "missing", Ct);
        Assert.Equal(["c", "a"], store.Items.Select(p => p.Id));
    }

    [Fact]
    public async Task A_full_sidebar_refuses_new_pins()
    {
        var store = Create();
        for (var i = 0; i < ISidebarPins.MaxCount; i++)
        {
            Assert.True(await store.PinAsync(Pin(PinKind.Album, $"al{i}", $"Album {i}"), Ct));
        }

        Assert.False(await store.PinAsync(Pin(PinKind.Album, "one-more", "One more"), Ct));
        Assert.True(await store.PinAsync(Pin(PinKind.Album, "al3", "Album 3"), Ct));
        Assert.Equal(ISidebarPins.MaxCount, store.Items.Count);
    }

    [Fact]
    public async Task Refresh_updates_a_pinned_item_only()
    {
        var store = Create();
        await store.PinAsync(Pin(PinKind.Playlist, "PL1", "Old name", "https://example.com/old.jpg"), Ct);
        var changes = 0;
        store.Changed += (_, _) => changes++;

        await store.RefreshAsync(Pin(PinKind.Playlist, "VLPL1", "New name", null), Ct);
        await store.RefreshAsync(Pin(PinKind.Playlist, "PL1", "New name", null), Ct); // nothing new
        await store.RefreshAsync(Pin(PinKind.Playlist, "PL2", "Not pinned"), Ct);

        var pin = Assert.Single(store.Items);
        Assert.Equal("New name", pin.Title);
        Assert.Equal("https://example.com/old.jpg", pin.ThumbnailUrl);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task An_unreadable_file_starts_empty_and_bad_entries_are_skipped()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, SidebarPinsStore.FileName), "{ not json", Ct);
        var broken = Create();
        await broken.LoadAsync(Ct);
        Assert.Empty(broken.Items);

        await File.WriteAllTextAsync(
            Path.Combine(_root, SidebarPinsStore.FileName),
            """
            { "version": 1, "pins": [
              { "kind": "album", "id": "MPREb_1", "title": "Kept" },
              { "kind": "album", "id": " ", "title": "No id" },
              { "kind": "station", "id": "s1", "title": "No station" },
              { "kind": "album", "id": "MPREb_1", "title": "Twice" }
            ] }
            """,
            Ct);
        var store = Create();
        await store.LoadAsync(Ct);

        var pin = Assert.Single(store.Items);
        Assert.Equal("Kept", pin.Title);
    }

    [Fact]
    public async Task Signing_out_or_switching_account_unpins_playlists_only()
    {
        var auth = new PinsAuth(AuthStatus.SignedOut);
        var store = Create(auth);
        await ((IHostedService)store).StartAsync(Ct);
        await store.PinAsync(Pin(PinKind.Playlist, "PL1", "Mine"), Ct);
        await store.PinAsync(Pin(PinKind.Album, "MPREb_1", "Album"), Ct);
        await store.PinAsync(Pin(PinKind.Artist, "UC1", "Artist"), Ct);
        await store.PinAsync(SidebarPin.For(Station("s1"))!, Ct);

        // The stored session restored at startup keeps them.
        auth.Raise(AuthStatus.SignedIn);
        await store.WhenForgottenAsync();
        Assert.True(store.Contains(PinKind.Playlist, "PL1"));

        // Signing in again over the session: maybe another account.
        auth.Raise(AuthStatus.SignedIn);
        await store.WhenForgottenAsync();
        Assert.Equal([PinKind.Album, PinKind.Artist, PinKind.Station], store.Items.Select(p => p.Kind));

        await store.PinAsync(Pin(PinKind.Playlist, "PL2", "Next"), Ct);
        auth.Raise(AuthStatus.Expired);
        await store.WhenForgottenAsync();
        Assert.True(store.Contains(PinKind.Playlist, "PL2"));
        auth.Raise(AuthStatus.SignedIn);
        await store.WhenForgottenAsync();
        Assert.False(store.Contains(PinKind.Playlist, "PL2"));

        await store.PinAsync(Pin(PinKind.Playlist, "PL3", "Signed out soon"), Ct);
        auth.Raise(AuthStatus.SignedOut);
        await store.WhenForgottenAsync();
        Assert.False(store.Contains(PinKind.Playlist, "PL3"));

        // Pinned while signed out: a second "signed out" (nothing changed) keeps it.
        await store.PinAsync(Pin(PinKind.Playlist, "PL4", "Public"), Ct);
        auth.Raise(AuthStatus.SignedOut);
        await store.WhenForgottenAsync();
        Assert.True(store.Contains(PinKind.Playlist, "PL4"));
        await ((IHostedService)store).StopAsync(Ct);

        var reloaded = Create();
        await reloaded.LoadAsync(Ct);
        Assert.Equal(["MPREb_1", "UC1", "s1", "PL4"], reloaded.Items.Select(p => p.Id));
    }

    private SidebarPinsStore Create(IAuthService? auth = null) =>
        new(new AppPaths(_root), auth ?? new PinsAuth(AuthStatus.SignedOut), NullLogger<SidebarPinsStore>.Instance);

    private static SidebarPin Pin(PinKind kind, string id, string title, string? thumbnail = null) =>
        new() { Kind = kind, Id = id, Title = title, ThumbnailUrl = thumbnail };

    private static RadioStation Station(string id) => new()
    {
        Id = id,
        Name = "Station " + id,
        StreamUrl = "http://example.com/" + id,
        LogoUrl = $"https://example.com/{id}.png",
        Tags = ["jazz"],
    };

    private sealed class PinsAuth(AuthStatus status) : IAuthService
    {
        public AuthStatus Status { get; private set; } = status;

        public AuthMode Mode => Status is AuthStatus.SignedIn or AuthStatus.Expired ? AuthMode.Cookies : AuthMode.None;

        public event EventHandler<AuthStatusChangedEventArgs>? StatusChanged;

        public void Raise(AuthStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, new AuthStatusChangedEventArgs(status, Mode));
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SignInWithCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SignInWithCookieHeaderAsync(string cookieHeader, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SignOutAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
