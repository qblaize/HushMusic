using System.Text.Json.Nodes;
using HushMusic.InnerTube.Parsing.Common;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class SignInDetectorTests
{
    public static TheoryData<string> RealFixtures => new(ParserFixtures.RealFixtureNames());

    public static TheoryData<string> SignedInSynthetic => new(ParserFixtures.SyntheticFixtureNames("synthetic_"));

    public static TheoryData<string> SignedOutCaptures => new(ParserFixtures.SyntheticFixtureNames("anon_signed_out_"));

    [Theory]
    [MemberData(nameof(SignedOutCaptures))]
    public void Signed_out_responses_are_detected(string fixture)
    {
        Assert.True(SignInDetector.LooksSignedOut(ParserFixtures.Synthetic(fixture)));
    }

    // Anonymous public pages still contain signInEndpoints inside item menus (like / save sign-in modals);
    // those must not count.
    [Theory]
    [MemberData(nameof(RealFixtures))]
    public void Public_pages_are_not_sign_in_prompts(string fixture)
    {
        Assert.False(SignInDetector.LooksSignedOut(ParserFixtures.Load(fixture)));
    }

    [Theory]
    [MemberData(nameof(SignedInSynthetic))]
    public void Library_shaped_pages_are_not_sign_in_prompts(string fixture)
    {
        Assert.False(SignInDetector.LooksSignedOut(ParserFixtures.Synthetic(fixture)));
    }

    [Fact]
    public void Empty_object_is_not_a_sign_in_prompt()
    {
        Assert.False(SignInDetector.LooksSignedOut(new JsonObject()));
    }
}
