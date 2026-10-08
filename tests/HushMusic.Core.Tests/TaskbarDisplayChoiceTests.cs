using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class TaskbarDisplayChoiceTests
{
    private static readonly DisplayInfo[] TwoDisplays =
    [
        new(@"\\.\DISPLAY1", "E27q-20", IsPrimary: true),
        new(@"\\.\DISPLAY2", "DELL U2720Q", IsPrimary: false),
    ];

    [Theory]
    [InlineData(null, "Primary")]
    [InlineData("", "Primary")]
    [InlineData("primary", "Primary")]
    [InlineData("ALL", "All")]
    [InlineData(@" \\.\DISPLAY2 ", @"\\.\DISPLAY2")]
    public void Normalizes_the_setting(string? setting, string expected) =>
        Assert.Equal(expected, TaskbarDisplayChoice.Normalize(setting));

    [Fact]
    public void Primary_is_the_main_taskbar() =>
        Assert.Equal(new string?[] { null }, TaskbarDisplayChoice.Targets("Primary", TwoDisplays));

    [Fact]
    public void All_is_every_display() =>
        Assert.Equal([@"\\.\DISPLAY1", @"\\.\DISPLAY2"], TaskbarDisplayChoice.Targets("All", TwoDisplays));

    [Fact]
    public void All_without_known_displays_is_the_main_taskbar() =>
        Assert.Equal(new string?[] { null }, TaskbarDisplayChoice.Targets("All", []));

    [Fact]
    public void One_display_is_matched_by_device_name_in_any_case() =>
        Assert.Equal([@"\\.\DISPLAY2"], TaskbarDisplayChoice.Targets(@"\\.\display2", TwoDisplays));

    [Fact]
    public void A_display_that_is_not_connected_falls_back_to_the_main_taskbar() =>
        Assert.Equal(new string?[] { null }, TaskbarDisplayChoice.Targets(@"\\.\DISPLAY3", TwoDisplays));

    [Fact]
    public void Labels_use_the_display_number_and_the_monitor_name()
    {
        Assert.Equal("Display 2 — DELL U2720Q", TaskbarDisplayChoice.Label(TwoDisplays[1]));
        Assert.Equal("Display 7", TaskbarDisplayChoice.Label(new DisplayInfo(@"\\.\DISPLAY7", null, false)));
        Assert.Equal("Odd name", TaskbarDisplayChoice.ShortName("Odd name"));
    }
}
