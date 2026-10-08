using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class GlobalHotkeyTests
{
    private const HotkeyModifiers CtrlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;

    [Theory]
    [InlineData("Ctrl+Alt+Space", CtrlAlt, 0x20)]
    [InlineData("ctrl + alt + right", CtrlAlt, 0x27)]
    [InlineData("Alt+Control+M", CtrlAlt, 0x4D)]
    [InlineData("Ctrl+Alt+Shift+F12", CtrlAlt | HotkeyModifiers.Shift, 0x7B)]
    [InlineData("Win+Shift+S", HotkeyModifiers.Windows | HotkeyModifiers.Shift, 0x53)]
    [InlineData("Windows+5", HotkeyModifiers.Windows, 0x35)]
    [InlineData("Shift+F5", HotkeyModifiers.Shift, 0x74)]
    [InlineData("Ctrl+Alt+-", CtrlAlt, 0xBD)]
    [InlineData("Ctrl+Alt+Plus", CtrlAlt, 0xBB)]
    [InlineData("Ctrl+Num-", HotkeyModifiers.Control, 0x6D)]
    [InlineData("Ctrl+NumPlus", HotkeyModifiers.Control, 0x6B)]
    [InlineData("Ctrl+Num7", HotkeyModifiers.Control, 0x67)]
    [InlineData("Ctrl+PgDn", HotkeyModifiers.Control, 0x22)]
    [InlineData("Ctrl+Alt+\\", CtrlAlt, 0xDC)]
    public void Parses_gestures(string text, HotkeyModifiers modifiers, int key)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var gesture));
        Assert.Equal(new HotkeyGesture(modifiers, key), gesture);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Space")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+Alt+")]
    [InlineData("Ctrl++")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Escape")]
    [InlineData("Ctrl+Backspace")]
    [InlineData("Ctrl+Alt+NoSuchKey")]
    [InlineData("Shift+A")]
    [InlineData("Shift+Space")]
    [InlineData("Shift+Tab")]
    [InlineData("Shift+Num5")]
    public void Rejects_unusable_gestures(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out _));
        Assert.Null(HotkeyGesture.Parse(text));
    }

    [Theory]
    [InlineData(CtrlAlt, 0x20, "Ctrl+Alt+Space")]
    [InlineData(CtrlAlt | HotkeyModifiers.Shift | HotkeyModifiers.Windows, 0x4B, "Win+Ctrl+Alt+Shift+K")]
    [InlineData(HotkeyModifiers.Shift, 0x87, "Shift+F24")]
    [InlineData(HotkeyModifiers.Control, 0xDE, "Ctrl+'")]
    [InlineData(HotkeyModifiers.Alt, 0x60, "Alt+Num0")]
    public void Formats_in_a_fixed_modifier_order(HotkeyModifiers modifiers, int key, string expected) =>
        Assert.Equal(expected, new HotkeyGesture(modifiers, key).ToString());

    [Fact]
    public void Every_nameable_key_round_trips_with_ctrl()
    {
        for (var key = 0; key < 256; key++)
        {
            if (HotkeyKeys.NameOf(key) is null)
            {
                continue;
            }

            var gesture = new HotkeyGesture(HotkeyModifiers.Control, key);
            Assert.Equal(gesture, HotkeyGesture.Parse(gesture.ToString()));
        }
    }

    [Theory]
    [InlineData(0x10)]
    [InlineData(0x11)]
    [InlineData(0x12)]
    [InlineData(0x5B)]
    [InlineData(0xA3)]
    public void Modifier_keys_are_not_keys_of_their_own(int key)
    {
        Assert.True(HotkeyKeys.IsModifier(key));
        Assert.Null(HotkeyKeys.NameOf(key));
    }

    [Fact]
    public void Defaults_are_valid_and_distinct()
    {
        var defaults = GlobalHotkeyMap.Actions.Select(GlobalHotkeyMap.DefaultFor).ToList();

        Assert.All(defaults, g => Assert.True(g.IsValid));
        Assert.Equal(defaults.Count, defaults.Distinct().Count());
        Assert.Equal("Ctrl+Alt+Space", GlobalHotkeyMap.DefaultFor(HotkeyAction.PlayPause).ToString());
        Assert.Equal("Ctrl+Alt+H", GlobalHotkeyMap.DefaultFor(HotkeyAction.ShowHide).ToString());
    }

    [Fact]
    public void Missing_actions_use_the_defaults_and_empty_ones_have_none()
    {
        var saved = new Dictionary<string, string>
        {
            ["Next"] = "Ctrl+Shift+Right",
            ["mute"] = "",
            ["Like"] = "not a gesture",
        };

        var map = GlobalHotkeyMap.ResolveAll(saved);

        Assert.Equal(HotkeyGesture.Parse("Ctrl+Shift+Right"), map[HotkeyAction.Next]);
        Assert.False(map.ContainsKey(HotkeyAction.Mute));
        Assert.Equal(GlobalHotkeyMap.DefaultFor(HotkeyAction.Like), map[HotkeyAction.Like]);
        Assert.Equal(GlobalHotkeyMap.DefaultFor(HotkeyAction.PlayPause), map[HotkeyAction.PlayPause]);
        Assert.Equal(7, map.Count);
    }

    [Fact]
    public void Without_defaults_only_saved_gestures_count()
    {
        var saved = new Dictionary<string, string> { ["PlayPause"] = "Ctrl+Alt+Shift+F12", ["Next"] = "" };

        var map = GlobalHotkeyMap.ResolveAll(saved, useDefaults: false);

        Assert.Equal([HotkeyAction.PlayPause], map.Keys);
        Assert.Null(GlobalHotkeyMap.Resolve(null, HotkeyAction.Next, useDefaults: false));
    }

    [Fact]
    public void A_gesture_used_twice_belongs_to_the_first_action()
    {
        var saved = new Dictionary<string, string> { ["ShowHide"] = "Ctrl+Alt+Space", ["Like"] = "Ctrl+Alt+Space" };

        var conflicts = GlobalHotkeyMap.Conflicts(GlobalHotkeyMap.ResolveAll(saved));

        Assert.Equal(2, conflicts.Count);
        Assert.Equal(HotkeyAction.PlayPause, conflicts[HotkeyAction.Like]);
        Assert.Equal(HotkeyAction.PlayPause, conflicts[HotkeyAction.ShowHide]);
    }

    [Fact]
    public void Settings_values_round_trip()
    {
        Assert.Equal(string.Empty, GlobalHotkeyMap.ToSetting(null));
        var gesture = GlobalHotkeyMap.DefaultFor(HotkeyAction.VolumeDown);
        Assert.Equal(gesture, HotkeyGesture.Parse(GlobalHotkeyMap.ToSetting(gesture)));
    }
}
