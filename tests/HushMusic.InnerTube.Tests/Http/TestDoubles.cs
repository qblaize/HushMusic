using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using HushMusic.Core.Abstractions;
using HushMusic.InnerTube.Api;
using HushMusic.InnerTube.Http;

namespace HushMusic.InnerTube.Tests.Http;

/// <summary>A request as the fake handler saw it (headers include content headers).</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public JsonObject Json => JsonNode.Parse(Body ?? throw new InvalidOperationException("Request has no body."))!.AsObject();

    /// <summary>The body without the client-added <c>context</c>, serialized compactly in its original key order.</summary>
    public string BodyWithoutContext
    {
        get
        {
            var json = Json;
            json.Remove("context");
            return json.ToJsonString();
        }
    }

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>Scripted HTTP handler: no network. Unscripted requests get <see cref="Default"/>.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<RecordedRequest, HttpResponseMessage>> _script = new();

    public List<RecordedRequest> Requests { get; } = [];

    public Func<RecordedRequest, HttpResponseMessage> Default { get; set; } = _ => Json("{}");

    public RecordedRequest Last => Requests[^1];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public void Enqueue(Func<RecordedRequest, HttpResponseMessage> responder) => _script.Enqueue(responder);

    public void EnqueueJson(string json, HttpStatusCode status = HttpStatusCode.OK) => Enqueue(_ => Json(json, status));

    public void EnqueueStatus(HttpStatusCode status) => Enqueue(_ => new HttpResponseMessage(status));

    public void EnqueueFixture(string name) => EnqueueJson(Fixtures.Read(name));

    public void EnqueueNetworkError() => Enqueue(_ => throw new HttpRequestException("connection reset"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers.NonValidated)
        {
            headers[name] = values.ToString();
        }

        string? body = null;
        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers.NonValidated)
            {
                headers[name] = values.ToString();
            }

            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        var recorded = new RecordedRequest(request.Method, request.RequestUri!, headers, body);
        Requests.Add(recorded);

        var responder = _script.Count > 0 ? _script.Dequeue() : Default;
        var response = responder(recorded);
        response.RequestMessage = request;
        return response;
    }
}

internal sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
}

internal sealed class FakeAuthenticator : IRequestAuthenticator
{
    public const string CookieHeader = "SID=sid; __Secure-3PAPISID=abc/def; SOCS=CAI";

    public bool IsAuthenticated { get; set; }

    public AuthMode Mode => IsAuthenticated ? AuthMode.Cookies : AuthMode.None;

    public int ApplyCount { get; private set; }

    public int ResponseCount { get; private set; }

    public List<string> Rejections { get; } = [];

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        ApplyCount++;

        // Same contract as the real AuthService: needs an absolute URI, replaces credential headers.
        if (request.RequestUri is not { IsAbsoluteUri: true })
        {
            throw new ArgumentException("RequestUri must be absolute.", nameof(request));
        }

        if (IsAuthenticated)
        {
            request.Headers.Remove("Cookie");
            request.Headers.TryAddWithoutValidation("Cookie", CookieHeader);
            request.Headers.TryAddWithoutValidation("Authorization", "SAPISIDHASH 1700000000_163e661c1ad4177128b563dfbe8b0ebfd1296a14");
            request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
        }

        return Task.CompletedTask;
    }

    public Task OnResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken = default)
    {
        ResponseCount++;
        return Task.CompletedTask;
    }

    public void ReportSessionRejected(string reason) => Rejections.Add(reason);
}

internal sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; } = new();

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default)
    {
        update(Current);
        return Task.CompletedTask;
    }
}

internal sealed class TempAppPaths : IAppPaths, IDisposable
{
    public TempAppPaths()
    {
        Root = Path.Combine(Path.GetTempPath(), "hushmusic-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Logs => Ensure("logs");

    public string Cache => Ensure("cache");

    public string ImageCache => Ensure(Path.Combine("cache", "images"));

    public string Secure => Ensure("secure");

    public string Tools => Ensure("tools");

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Ensure(string relative)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(path);
        return path;
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>Wires the real client and API classes to fakes.</summary>
internal sealed class InnerTubeTestHost : IDisposable
{
    public const string VisitorId = "CgtWSVNJVE9SSUQ%3D";

    public static readonly DateTimeOffset Now = new(2026, 10, 7, 23, 30, 0, TimeSpan.Zero);

    public InnerTubeTestHost(bool seedVisitorId = true)
    {
        if (seedVisitorId)
        {
            File.WriteAllText(Path.Combine(Paths.Cache, "visitor_id.txt"), VisitorId);
        }

        var factory = new FakeHttpClientFactory(Handler);
        Visitor = new VisitorIdProvider(factory, Paths, Time, NullLogger<VisitorIdProvider>.Instance);
        Client = new InnerTubeClient(
            factory,
            Auth,
            Settings,
            Visitor,
            Options.Create(new InnerTubeOptions { RetryDelay = TimeSpan.Zero }),
            Time,
            NullLogger<InnerTubeClient>.Instance);
    }

    public FakeHttpHandler Handler { get; } = new();

    public FakeAuthenticator Auth { get; } = new();

    public FakeSettingsService Settings { get; } = new();

    public TempAppPaths Paths { get; } = new();

    public FixedTimeProvider Time { get; } = new(Now);

    public VisitorIdProvider Visitor { get; }

    public InnerTubeClient Client { get; }

    public BrowseApi Browse => new(Client, NullLogger<BrowseApi>.Instance);

    public SearchApi Search => new(Client, NullLogger<SearchApi>.Instance);

    public LibraryApi Library => new(Client, NullLogger<LibraryApi>.Instance);

    public WatchApi Watch => new(Client, Time, NullLogger<WatchApi>.Instance);

    public AccountApi Account => new(Client, Time, NullLogger<AccountApi>.Instance);

    public void SignIn() => Auth.IsAuthenticated = true;

    public void Dispose()
    {
        Handler.Dispose();
        Paths.Dispose();
    }
}

/// <summary>Small response bodies shared by tests.</summary>
internal static class Responses
{
    public const string EmptyQueue =
        """{"contents":{"singleColumnMusicWatchNextResultsRenderer":{"tabbedRenderer":{"watchNextTabbedResultsRenderer":{"tabs":[{"tabRenderer":{"content":{"musicQueueRenderer":{"content":{"playlistPanelRenderer":{"contents":[]}}}}}}]}}}}}""";

    public const string Succeeded = """{"status":"STATUS_SUCCEEDED"}""";

    // Trimmed from an anonymous browse FEmusic_history response (2026-10-07).
    public const string SignInPrompt =
        """{"contents":{"singleColumnBrowseResultsRenderer":{"tabs":[{"tabRenderer":{"selected":true,"content":{"sectionListRenderer":{"contents":[{"itemSectionRenderer":{"contents":[{"messageRenderer":{"text":{"runs":[{"text":"Sign in to view your history"}]},"icon":{"iconType":"WATCH_HISTORY"},"button":{"buttonRenderer":{"style":"STYLE_DEFAULT","text":{"runs":[{"text":"Sign in"}]},"navigationEndpoint":{"signInEndpoint":{"hack":true}}}}}}]}}]}}}}]}}}""";

    // Trimmed from an anonymous account/account_menu response (2026-10-07).
    public const string SignedOutAccountMenu =
        """{"actions":[{"openPopupAction":{"popup":{"multiPageMenuRenderer":{"sections":[{"multiPageMenuSectionRenderer":{"items":[{"compactLinkRenderer":{"title":{"runs":[{"text":"Settings"}]}}}]}}],"style":"MULTI_PAGE_MENU_STYLE_TYPE_ACCOUNT"}},"popupType":"DROPDOWN"}}]}""";

    public const string Unauthenticated =
        """{"error":{"code":401,"message":"You must be signed in to perform this operation.","errors":[{"message":"You must be signed in to perform this operation.","domain":"global","reason":"unauthorized"}],"status":"UNAUTHENTICATED"}}""";

    public const string InvalidArgument =
        """{"error":{"code":400,"message":"Request contains an invalid argument.","errors":[{"message":"Request contains an invalid argument.","domain":"global","reason":"badRequest"}],"status":"INVALID_ARGUMENT"}}""";
}
