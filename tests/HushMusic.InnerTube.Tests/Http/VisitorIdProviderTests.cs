using System.Net;
using System.Text.Json.Nodes;
using HushMusic.InnerTube.Http;
using Xunit;

namespace HushMusic.InnerTube.Tests.Http;

public sealed class VisitorIdProviderTests : IDisposable
{
    // Shape of the music.youtube.com page: several ytcfg.set blocks; ytmusicapi reads the first one.
    private const string Page =
        """<html><script>ytcfg.set({"VISITOR_DATA":"CgtGRVRDSEVE%3D%3D","INNERTUBE_API_KEY":"k"}); ytcfg.set({"VISITOR_DATA":"SECOND"});</script></html>""";

    private readonly InnerTubeTestHost _host = new(seedVisitorId: false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string CacheFile => Path.Combine(_host.Paths.Cache, "visitor_id.txt");

    public void Dispose() => _host.Dispose();

    [Fact]
    public void Extracts_visitor_data_from_the_first_ytcfg_block()
    {
        Assert.Equal("CgtGRVRDSEVE%3D%3D", VisitorIdProvider.ExtractVisitorId(Page));
        Assert.Null(VisitorIdProvider.ExtractVisitorId("<html>no config</html>"));
    }

    [Fact]
    public async Task Fetches_anonymously_once_then_caches_in_memory_and_on_disk()
    {
        _host.SignIn();
        _host.Handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Page) });

        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct);
        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct);

        var get = _host.Handler.Requests[0];
        Assert.Equal(HttpMethod.Get, get.Method);
        Assert.Equal("https://music.youtube.com/", get.Uri.AbsoluteUri);
        Assert.Equal("SOCS=CAI", get.Header("Cookie"));
        Assert.Null(get.Header("Authorization"));

        Assert.Equal(3, _host.Handler.Requests.Count);
        Assert.All(_host.Handler.Requests.Skip(1), r => Assert.Equal("CgtGRVRDSEVE%3D%3D", r.Header("X-Goog-Visitor-Id")));
        Assert.Equal("CgtGRVRDSEVE%3D%3D", await File.ReadAllTextAsync(CacheFile, Ct));
    }

    [Fact]
    public async Task Uses_the_persisted_value_without_fetching()
    {
        await File.WriteAllTextAsync(CacheFile, "FROMDISK", Ct);

        Assert.Equal("FROMDISK", await _host.Visitor.GetAsync(Ct));
        Assert.Empty(_host.Handler.Requests);
    }

    [Fact]
    public async Task Failed_fetch_sends_an_empty_header_and_does_not_fail_the_request()
    {
        _host.Handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);

        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct);

        Assert.Equal(string.Empty, _host.Handler.Last.Header("X-Goog-Visitor-Id"));
        Assert.False(File.Exists(CacheFile));
    }
}
