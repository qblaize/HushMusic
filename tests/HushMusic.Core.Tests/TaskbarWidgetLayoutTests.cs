using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class TaskbarWidgetLayoutTests
{
    // The user's taskbar: 2560 x 48 at 100 %, centred icons from 971 to 1590, notification area from 2304.
    private static TaskbarContent Centred(params PixelSpan[] occupied) =>
        new(2560, 48, 1.0, new PixelSpan(971, 1590), 2304, occupied);

    [Fact]
    public void Centred_icons_put_the_player_at_the_left_end()
    {
        var place = TaskbarWidgetLayout.Place(Centred());

        Assert.Equal(new PixelRect(4, 0, 288, 48), place);
    }

    [Fact]
    public void Other_taskbar_content_on_the_left_is_kept_clear()
    {
        // e.g. the Widgets button or another media widget at 4..241.
        var place = TaskbarWidgetLayout.Place(Centred(new PixelSpan(4, 241)));

        Assert.Equal(new PixelRect(253, 0, 288, 48), place);
    }

    [Fact]
    public void Width_shrinks_to_the_space_before_the_start_button()
    {
        var taskbar = new TaskbarContent(1280, 48, 1.0, new PixelSpan(250, 900), 1100, []);

        var place = TaskbarWidgetLayout.Place(taskbar);

        // 4 .. 250 - 12 gap = 234 px wide.
        Assert.Equal(new PixelRect(4, 0, 234, 48), place);
    }

    [Fact]
    public void Left_aligned_icons_put_the_player_before_the_notification_area()
    {
        var taskbar = new TaskbarContent(1920, 48, 1.0, new PixelSpan(0, 700), 1700, []);

        var place = TaskbarWidgetLayout.Place(taskbar);

        Assert.Equal(new PixelRect(1700 - 12 - 288, 0, 288, 48), place);
    }

    [Fact]
    public void No_room_hides_the_player()
    {
        var taskbar = new TaskbarContent(1366, 48, 1.0, new PixelSpan(0, 1200), 1250, []);

        Assert.Null(TaskbarWidgetLayout.Place(taskbar));
    }

    [Fact]
    public void Unknown_icons_or_a_vertical_taskbar_hide_the_player()
    {
        Assert.Null(TaskbarWidgetLayout.Place(new TaskbarContent(2560, 48, 1.0, null, 2304, [])));
        Assert.Null(TaskbarWidgetLayout.Place(new TaskbarContent(48, 1400, 1.0, new PixelSpan(0, 48), 48, [])));
    }

    [Fact]
    public void Placement_scales_with_dpi()
    {
        var taskbar = new TaskbarContent(3840, 72, 1.5, new PixelSpan(1450, 2390), 3450, []);

        var place = TaskbarWidgetLayout.Place(taskbar);

        Assert.Equal(new PixelRect(6, 0, 432, 72), place);
    }

    [Fact]
    public void Icon_group_is_the_run_of_buttons_next_to_start()
    {
        // As read from the user's taskbar: Start, three app buttons 2 px apart, a media mod at the far left (nested parts).
        PixelSpan start = new(993, 1038);
        PixelSpan[] spans = [start, new(1040, 1084), new(1084, 1128), new(1128, 1172), new(4, 262), new(8, 40), new(172, 200)];

        var (icons, others) = TaskbarWidgetLayout.SplitIcons(spans, start, maxGap: 8);

        Assert.Equal(new PixelSpan(993, 1172), icons);
        Assert.Equal([new PixelSpan(4, 262), new PixelSpan(8, 40), new PixelSpan(172, 200)], others);
    }

    [Fact]
    public void Icon_group_keeps_a_distant_widgets_button_apart()
    {
        PixelSpan start = new(900, 945);
        PixelSpan[] spans = [new(0, 160), new(1500, 1544), new(946, 990), start];

        var (icons, others) = TaskbarWidgetLayout.SplitIcons(spans, start, maxGap: 8);

        Assert.Equal(new PixelSpan(900, 990), icons);
        Assert.Equal([new PixelSpan(0, 160), new PixelSpan(1500, 1544)], others);
    }

    [Fact]
    public void Geometry_fits_cover_text_and_buttons_inside_the_width()
    {
        var g = TaskbarWidgetLayout.Measure(288, 48, 1.0);

        Assert.Equal(new PixelRect(0, 4, 288, 40), g.Highlight);
        Assert.Equal(new PixelRect(4, 8, 32, 32), g.Cover);
        Assert.Equal(new PixelRect(252, 8, 32, 32), g.Next);
        Assert.Equal(g.Next.X, g.PlayPause.Right);
        Assert.Equal(g.PlayPause.X, g.Previous.Right);
        Assert.Equal(g.Cover.Right + 8, g.Text.X);
        Assert.Equal(g.Previous.X - 4, g.Text.Right);
        Assert.True(g.Progress.Width > 0 && g.Progress.Height == 2);
    }

    [Fact]
    public void Geometry_scales_with_dpi()
    {
        var g = TaskbarWidgetLayout.Measure(432, 72, 1.5);

        Assert.Equal(48, g.Cover.Width);
        Assert.Equal(48, g.Next.Width);
        Assert.Equal(3, g.Progress.Height);
        Assert.Equal(432 - 6, g.Next.Right);
    }

    [Theory]
    [InlineData(10, 24, TaskbarWidgetPart.Content)]
    [InlineData(150, 24, TaskbarWidgetPart.Content)]
    [InlineData(190, 24, TaskbarWidgetPart.Previous)]
    [InlineData(230, 2, TaskbarWidgetPart.PlayPause)]
    [InlineData(283, 46, TaskbarWidgetPart.Next)]
    [InlineData(286, 24, TaskbarWidgetPart.None)]
    [InlineData(-1, 24, TaskbarWidgetPart.None)]
    [InlineData(100, 48, TaskbarWidgetPart.None)]
    public void Hit_test_finds_the_part_under_the_pointer(int x, int y, TaskbarWidgetPart expected)
    {
        var g = TaskbarWidgetLayout.Measure(288, 48, 1.0);

        Assert.Equal(expected, TaskbarWidgetLayout.HitTest(g, x, y));
    }

    [Theory]
    [InlineData(43, 1, 45)]
    [InlineData(45, 1, 50)]
    [InlineData(43, -1, 40)]
    [InlineData(45, -1, 40)]
    [InlineData(43, 3, 55)]
    [InlineData(98, 1, 100)]
    [InlineData(100, 1, 100)]
    [InlineData(2, -1, 0)]
    [InlineData(0, -2, 0)]
    [InlineData(80, 0, 80)]
    public void Volume_steps_to_multiples_of_five_and_stays_in_range(double volume, int notches, double expected) =>
        Assert.Equal(expected, TaskbarWidgetLayout.StepVolume(volume, notches));

    [Fact]
    public void Wheel_deltas_add_up_to_whole_notches()
    {
        var pending = 0;

        Assert.Equal(1, TaskbarWidgetLayout.TakeNotches(ref pending, 120));
        Assert.Equal(0, TaskbarWidgetLayout.TakeNotches(ref pending, 60));
        Assert.Equal(1, TaskbarWidgetLayout.TakeNotches(ref pending, 60));
        Assert.Equal(0, pending);
        Assert.Equal(-2, TaskbarWidgetLayout.TakeNotches(ref pending, -240));
    }

    [Fact]
    public void Turning_the_wheel_back_drops_the_pending_fraction()
    {
        var pending = 0;
        TaskbarWidgetLayout.TakeNotches(ref pending, 100);

        Assert.Equal(0, TaskbarWidgetLayout.TakeNotches(ref pending, -100));
        Assert.Equal(-100, pending);
    }
}
