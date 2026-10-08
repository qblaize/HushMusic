using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class AutoStartCommandTests
{
    private const string Exe = @"C:\Program Files\Hush\HushMusic.exe";

    [Fact]
    public void Build_quotes_the_path_and_adds_the_background_switch() =>
        Assert.Equal("\"C:\\Program Files\\Hush\\HushMusic.exe\" --background", AutoStartCommand.Build(Exe));

    [Theory]
    [InlineData("\"C:\\Program Files\\Hush\\HushMusic.exe\" --background", @"C:\Program Files\Hush\HushMusic.exe")]
    [InlineData(@"C:\Apps\HushMusic.exe --background", @"C:\Apps\HushMusic.exe")]
    [InlineData(@"C:\Apps\HushMusic.exe", @"C:\Apps\HushMusic.exe")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void ExecutableOf_reads_quoted_and_unquoted_commands(string? command, string? expected) =>
        Assert.Equal(expected, AutoStartCommand.ExecutableOf(command));

    [Fact]
    public void Enabled_writes_a_missing_entry() =>
        Assert.Equal(AutoStartAction.Write, AutoStartCommand.Reconcile(enabled: true, currentValue: null, Exe));

    [Fact]
    public void Enabled_keeps_a_current_entry() =>
        Assert.Equal(AutoStartAction.None, AutoStartCommand.Reconcile(enabled: true, AutoStartCommand.Build(Exe), Exe));

    [Fact]
    public void Enabled_rewrites_an_entry_for_a_moved_executable() =>
        Assert.Equal(AutoStartAction.Write, AutoStartCommand.Reconcile(enabled: true, AutoStartCommand.Build(@"D:\Old\HushMusic.exe"), Exe));

    [Fact]
    public void Disabled_deletes_this_executables_entry_case_insensitively() =>
        Assert.Equal(AutoStartAction.Delete, AutoStartCommand.Reconcile(enabled: false, AutoStartCommand.Build(Exe.ToUpperInvariant()), Exe));

    [Fact]
    public void Disabled_leaves_another_copys_entry_alone() =>
        Assert.Equal(AutoStartAction.None, AutoStartCommand.Reconcile(enabled: false, AutoStartCommand.Build(@"C:\Dev\bin\HushMusic.exe"), Exe));

    [Fact]
    public void Disabled_without_an_entry_does_nothing() =>
        Assert.Equal(AutoStartAction.None, AutoStartCommand.Reconcile(enabled: false, currentValue: null, Exe));

    [Theory]
    [InlineData(new[] { @"C:\Apps\HushMusic.exe", "--background" }, true)]
    [InlineData(new[] { @"C:\Apps\HushMusic.exe", "--BACKGROUND" }, true)]
    [InlineData(new[] { @"C:\Apps\HushMusic.exe" }, false)]
    [InlineData(new[] { @"C:\Apps\HushMusic.exe", "--background-music" }, false)]
    public void IsBackgroundLaunch_checks_the_arguments(string[] arguments, bool expected) =>
        Assert.Equal(expected, AutoStartCommand.IsBackgroundLaunch(arguments));

    [Theory]
    [InlineData("\"C:\\Program Files\\Hush\\HushMusic.exe\" --background", true)]
    [InlineData("\"C:\\Program Files\\--background\\HushMusic.exe\"", false)]
    [InlineData("HushMusic.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsBackgroundLaunch_parses_a_command_line(string? commandLine, bool expected) =>
        Assert.Equal(expected, AutoStartCommand.IsBackgroundLaunch(commandLine));
}
