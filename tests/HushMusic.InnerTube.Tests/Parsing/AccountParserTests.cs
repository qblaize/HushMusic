using System.Text.Json.Nodes;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class AccountParserTests
{
    private readonly CapturingLogger _log = new();

    [Fact]
    public void Signed_in_menu_returns_name_handle_and_largest_photo()
    {
        var account = AccountParser.ParseAccountInfo(ParserFixtures.Synthetic("synthetic_account_menu.json"), _log);

        Assert.NotNull(account);
        Assert.Equal("Sample User", account.Name);
        Assert.Equal("@SampleUser", account.ChannelHandle);
        Assert.Equal("https://yt3.ggpht.com/sample-user-photo=s176-c-k-c0x00ffffff-no-rj", account.PhotoUrl);
    }

    [Fact]
    public void Signed_out_menu_returns_null()
    {
        Assert.Null(AccountParser.ParseAccountInfo(ParserFixtures.Synthetic("anon_signed_out_account_menu.json"), _log));
    }

    [Fact]
    public void Header_without_name_returns_null_with_a_warning()
    {
        var response = ParserFixtures.Synthetic("synthetic_account_menu.json");
        response.At("actions", 0, "openPopupAction", "popup", "multiPageMenuRenderer", "header", "activeAccountHeaderRenderer")
            .AsObject().Remove("accountName");

        Assert.Null(AccountParser.ParseAccountInfo(response, _log));
        Assert.Contains(_log.Warnings, w => w.Contains("accountName", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_object_returns_null()
    {
        Assert.Null(AccountParser.ParseAccountInfo(new JsonObject(), _log));
    }
}
