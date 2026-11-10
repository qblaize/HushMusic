using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class TaskbarBackdropTests
{
    private const int Width = 300;
    private const int Height = 48;

    // BGRA pixels of one colour.
    private static byte[] Solid(byte r, byte g, byte b)
    {
        var pixels = new byte[Width * Height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    // Dark grass with a sprinkle of bright yellow flowers (every sixth pixel): a wallpaper seen through the taskbar.
    private static byte[] Meadow()
    {
        var pixels = Solid(34, 52, 20);
        for (var i = 0; i < pixels.Length; i += 4 * 6)
        {
            pixels[i] = 40;
            pixels[i + 1] = 210;
            pixels[i + 2] = 240;
        }

        return pixels;
    }

    [Fact]
    public void A_dark_taskbar_gets_light_text_without_a_shadow()
    {
        var reading = TaskbarBackdrop.Read(Solid(32, 32, 32), [])!.Value;

        Assert.True(TaskbarBackdrop.UseLightText(reading, lightText: false));
        Assert.False(TaskbarBackdrop.NeedsShadow(reading, lightText: true));
    }

    [Fact]
    public void A_light_taskbar_gets_dark_text_without_a_shadow()
    {
        var reading = TaskbarBackdrop.Read(Solid(238, 238, 238), [])!.Value;

        Assert.False(TaskbarBackdrop.UseLightText(reading, lightText: true));
        Assert.False(TaskbarBackdrop.NeedsShadow(reading, lightText: false));
    }

    [Fact]
    public void A_dark_photo_behind_a_light_mode_taskbar_gets_light_text_with_a_shadow()
    {
        var reading = TaskbarBackdrop.Read(Meadow(), [])!.Value;

        // Windows is in light mode (dark text), but the wallpaper behind the clear taskbar is mostly dark.
        Assert.True(TaskbarBackdrop.UseLightText(reading, lightText: false));
        Assert.True(TaskbarBackdrop.NeedsShadow(reading, lightText: true));
    }

    [Fact]
    public void A_mid_grey_keeps_the_current_colour()
    {
        // Readable either way (luminance about 0.2): no reason to switch, in either direction.
        var reading = TaskbarBackdrop.Read(Solid(124, 124, 124), [])!.Value;

        Assert.True(TaskbarBackdrop.UseLightText(reading, lightText: true));
        Assert.False(TaskbarBackdrop.UseLightText(reading, lightText: false));
    }

    [Fact]
    public void The_players_own_text_is_left_out()
    {
        // A light taskbar, with the player's white title over the left half of the sample.
        var screen = Solid(238, 238, 238);
        var overlay = new byte[screen.Length];
        for (var i = 0; i < screen.Length / 2; i += 4)
        {
            screen[i] = screen[i + 1] = screen[i + 2] = 255;
            overlay[i] = overlay[i + 1] = overlay[i + 2] = overlay[i + 3] = 255;
        }

        var reading = TaskbarBackdrop.Read(screen, overlay)!.Value;

        Assert.Equal(Width * Height / 2, reading.Samples);
        Assert.Equal(1.0, reading.LightShare);
    }

    [Fact]
    public void The_players_faint_fill_is_taken_back_out()
    {
        // Black backdrop under the player's almost invisible hit-test fill (premultiplied white at alpha 8).
        var screen = Solid(8, 8, 8);
        var overlay = new byte[screen.Length];
        for (var i = 0; i < overlay.Length; i += 4)
        {
            overlay[i] = overlay[i + 1] = overlay[i + 2] = overlay[i + 3] = 8;
        }

        var reading = TaskbarBackdrop.Read(screen, overlay)!.Value;

        Assert.Equal(1.0, reading.DarkShare);
    }

    [Fact]
    public void A_sample_mostly_covered_by_the_player_gives_no_answer()
    {
        // Hovered, or the volume bar showing: the player's own fill covers the whole rectangle.
        var overlay = new byte[Width * Height * 4];
        Array.Fill(overlay, (byte)40);

        Assert.Null(TaskbarBackdrop.Read(Solid(32, 32, 32), overlay));
    }

    [Fact]
    public void Mismatched_sizes_give_no_answer()
    {
        Assert.Null(TaskbarBackdrop.Read(Solid(32, 32, 32), new byte[16]));
        Assert.Null(TaskbarBackdrop.Read([], []));
    }
}
