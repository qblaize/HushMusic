using HushMusic.Playback.YtDlp;
using Xunit;

namespace HushMusic.Playback.Tests;

public sealed class JsRuntimeSelectorTests
{
    private const string ManagedDeno = @"C:\Data\tools\deno\2.9.7\deno.exe";
    private const string Node = @"C:\Program Files\nodejs\node.exe";
    private const string DenoOnPath = @"C:\Users\me\.deno\bin\deno.exe";

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Auto_uses_the_managed_deno_when_it_is_installed(string? setting)
    {
        var plan = JsRuntimeSelector.Choose(setting, ManagedDeno, Node, null);

        Assert.Equal(JsRuntimeSource.ManagedDeno, plan.Source);
        Assert.Equal(new[] { "--no-js-runtimes", "--js-runtimes", "deno:" + ManagedDeno }, plan.Arguments);
        Assert.False(plan.WantsManagedDeno);
    }

    [Fact]
    public void Auto_uses_node_until_the_managed_deno_is_installed()
    {
        var plan = JsRuntimeSelector.Choose("auto", null, Node, null);

        Assert.Equal(JsRuntimeSource.NodeOnPath, plan.Source);
        Assert.Equal(new[] { "--no-js-runtimes", "--js-runtimes", "node:" + Node }, plan.Arguments);
        Assert.True(plan.WantsManagedDeno);
    }

    [Fact]
    public void Auto_without_any_runtime_has_to_wait_for_the_managed_deno()
    {
        var plan = JsRuntimeSelector.Choose("auto", null, null, null);

        Assert.Equal(JsRuntimeSource.None, plan.Source);
        Assert.Empty(plan.Arguments);
        Assert.True(plan.WantsManagedDeno);
    }

    [Theory]
    [InlineData("node")]
    [InlineData(" Node ")]
    public void Node_keeps_using_node_when_it_is_installed(string setting)
    {
        var plan = JsRuntimeSelector.Choose(setting, ManagedDeno, Node, null);

        Assert.Equal(JsRuntimeSource.Setting, plan.Source);
        Assert.Equal(new[] { "--js-runtimes", setting.Trim() }, plan.Arguments);
        Assert.False(plan.WantsManagedDeno);
    }

    [Fact]
    public void A_saved_node_without_node_installed_behaves_like_auto()
    {
        Assert.Equal(JsRuntimeSource.ManagedDeno, JsRuntimeSelector.Choose("node", ManagedDeno, null, null).Source);

        var waiting = JsRuntimeSelector.Choose("node", null, null, null);
        Assert.Equal(JsRuntimeSource.None, waiting.Source);
        Assert.True(waiting.WantsManagedDeno);
    }

    [Fact]
    public void Deno_prefers_a_deno_on_path_and_falls_back_to_the_managed_one()
    {
        var onPath = JsRuntimeSelector.Choose("deno", ManagedDeno, Node, DenoOnPath);
        Assert.Equal(JsRuntimeSource.Setting, onPath.Source);
        Assert.Equal(new[] { "--js-runtimes", "deno" }, onPath.Arguments);

        var managed = JsRuntimeSelector.Choose("deno", ManagedDeno, Node, null);
        Assert.Equal(JsRuntimeSource.ManagedDeno, managed.Source);
    }

    [Theory]
    [InlineData(@"node:C:\tools\node.exe")]
    [InlineData(@"deno:D:\deno\deno.exe")]
    [InlineData("quickjs")]
    public void Explicit_values_pass_through_unchanged(string setting)
    {
        var plan = JsRuntimeSelector.Choose(setting, ManagedDeno, Node, DenoOnPath);

        Assert.Equal(JsRuntimeSource.Setting, plan.Source);
        Assert.Equal(new[] { "--js-runtimes", setting }, plan.Arguments);
        Assert.False(plan.WantsManagedDeno);
    }

    [Theory]
    [InlineData("auto", true, false)]
    [InlineData("node", true, false)]
    [InlineData("deno", true, true)]
    [InlineData(@"node:C:\node.exe", false, false)]
    public void Only_the_runtimes_a_setting_can_use_are_looked_up(string setting, bool node, bool deno)
    {
        Assert.Equal(node, JsRuntimeSelector.UsesNodeOnPath(setting));
        Assert.Equal(deno, JsRuntimeSelector.UsesDenoOnPath(setting));
    }

    [Fact]
    public void FindOnPath_returns_the_first_directory_that_has_the_file()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\b\node.exe", @"C:\c\node.exe" };

        var found = JsRuntimeSelector.FindOnPath("node.exe", @"C:\a;;  ""C:\b"" ;relative\dir;C:\c", existing.Contains);

        Assert.Equal(@"C:\b\node.exe", found);
    }

    [Fact]
    public void FindOnPath_skips_relative_entries_and_handles_a_missing_path()
    {
        Assert.Null(JsRuntimeSelector.FindOnPath("node.exe", @".;bin", _ => true));
        Assert.Null(JsRuntimeSelector.FindOnPath("node.exe", null, _ => true));
    }

    [Theory]
    [InlineData("v22.11.0\r\n", "22.11.0")]
    [InlineData("deno 2.9.7 (stable, release, x86_64-pc-windows-msvc)\nv8 14.1.146.4-rusty\ntypescript 5.9.2\n", "2.9.7")]
    [InlineData("v20.9.0", "20.9.0")]
    public void ParseVersionOutput_reads_node_and_deno_versions(string output, string expected) =>
        Assert.Equal(Version.Parse(expected), JsRuntimeSelector.ParseVersionOutput(output));

    [Theory]
    [InlineData("")]
    [InlineData("command not found")]
    public void ParseVersionOutput_returns_null_for_other_output(string output) =>
        Assert.Null(JsRuntimeSelector.ParseVersionOutput(output));

    [Fact]
    public void Old_node_builds_are_below_the_minimum()
    {
        Assert.True(JsRuntimeSelector.ParseVersionOutput("v20.9.0") < JsRuntimeSelector.MinimumNodeVersion);
        Assert.True(JsRuntimeSelector.ParseVersionOutput("v22.0.0") >= JsRuntimeSelector.MinimumNodeVersion);
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, false, false, false)]
    public void Playback_waits_only_for_what_it_cannot_do_without(bool ytDlp, bool deno, bool node, bool blocks) =>
        Assert.Equal(blocks, new MissingComponents(ytDlp, deno, node).BlocksPlayback);
}
