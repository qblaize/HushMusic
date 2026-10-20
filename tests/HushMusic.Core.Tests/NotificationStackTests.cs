using HushMusic.Core.Abstractions;
using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

/// <summary>The in-app toast stack: order of leaving, merging, waiting for room and holding.</summary>
public sealed class NotificationStackTests
{
    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);

    private static AppNotification Info(string title, string message = "") => new(NotificationSeverity.Informational, title, message);

    private static AppNotification Error(string title) => new(NotificationSeverity.Error, title, "Network error.");

    private static AppNotification WithButton(string title) =>
        new(NotificationSeverity.Informational, title, string.Empty) { Action = new NotificationAction("Undo", () => { }) };

    private static string[] Titles(NotificationStack stack) => [.. stack.Shown.Select(t => t.Notification.Title)];

    // ===== Leaving in order =====

    [Fact]
    public void A_toast_goes_by_itself_after_six_seconds()
    {
        var stack = new NotificationStack();
        stack.Add(Info("a"), Seconds(0));

        Assert.Equal(Seconds(6), stack.NextDue);
        Assert.False(stack.RemoveDue(Seconds(5.9)));
        Assert.True(stack.RemoveDue(Seconds(6)));
        Assert.Empty(stack.Shown);
        Assert.Null(stack.NextDue);
    }

    [Fact]
    public void Toasts_added_together_leave_one_at_a_time_oldest_first()
    {
        var stack = new NotificationStack();
        var a = stack.Add(Info("a"), Seconds(0));
        var b = stack.Add(Info("b"), Seconds(0.1));
        var c = stack.Add(Info("c"), Seconds(0.2));

        Assert.Equal(Seconds(6), a.DueAt);
        Assert.Equal(Seconds(6.4), b.DueAt);
        Assert.Equal(Seconds(6.8), c.DueAt);

        stack.RemoveDue(Seconds(6));
        Assert.Equal(["b", "c"], Titles(stack));
    }

    [Fact]
    public void A_newer_toast_never_goes_before_an_older_one()
    {
        var stack = new NotificationStack();
        var a = stack.Add(Info("a"), Seconds(0));
        stack.Add(Info("b"), Seconds(3));

        // "a" comes in again: it starts over (due at 10), so "b" waits until after it.
        stack.Add(Info("a"), Seconds(4));

        Assert.Equal(Seconds(10), a.DueAt);
        Assert.Equal(Seconds(10.4), stack.Shown[1].DueAt);
        Assert.Equal(["a", "b"], Titles(stack));
    }

    [Fact]
    public void Errors_and_toasts_with_a_button_stay_until_closed()
    {
        var stack = new NotificationStack();
        var error = stack.Add(Error("e"), Seconds(0));
        var undo = stack.Add(WithButton("u"), Seconds(0));

        Assert.True(error.IsSticky);
        Assert.True(undo.IsSticky);
        Assert.Null(stack.NextDue);
        Assert.False(stack.RemoveDue(Seconds(600)));

        Assert.True(stack.Remove(error, Seconds(601)));
        Assert.Equal(["u"], Titles(stack));
        Assert.False(stack.Remove(error, Seconds(602)));
    }

    // ===== Merging =====

    [Fact]
    public void The_same_message_again_counts_up_and_starts_over_instead_of_adding_a_card()
    {
        var stack = new NotificationStack();
        var first = stack.Add(Info("Added to queue", "3 songs"), Seconds(0));
        var second = stack.Add(Info("Added to queue", "3 songs"), Seconds(5));

        Assert.Same(first, second);
        Assert.Equal(2, first.Count);
        Assert.Single(stack.Shown);
        Assert.Equal(Seconds(11), first.DueAt);
    }

    [Fact]
    public void Different_text_or_severity_is_another_toast()
    {
        var stack = new NotificationStack();
        stack.Add(Info("Added to queue", "3 songs"), Seconds(0));
        stack.Add(Info("Added to queue", "4 songs"), Seconds(0));
        stack.Add(new AppNotification(NotificationSeverity.Warning, "Added to queue", "3 songs"), Seconds(0));

        Assert.Equal(3, stack.Shown.Count);
        Assert.All(stack.Shown, t => Assert.Equal(1, t.Count));
    }

    [Fact]
    public void Repeated_errors_merge_but_toasts_with_buttons_never_do()
    {
        var stack = new NotificationStack();
        stack.Add(Error("Couldn't load"), Seconds(0));
        stack.Add(Error("Couldn't load"), Seconds(1));
        stack.Add(WithButton("Song removed"), Seconds(2));
        stack.Add(WithButton("Song removed"), Seconds(3));

        Assert.Equal(3, stack.Shown.Count);
        Assert.Equal(2, stack.Shown[0].Count);
    }

    // ===== Waiting for room =====

    [Fact]
    public void Only_the_newest_three_are_shown_and_the_older_ones_wait()
    {
        var stack = new NotificationStack();
        var error = stack.Add(Error("e"), Seconds(0));
        stack.Add(Info("a"), Seconds(1));
        stack.Add(Info("b"), Seconds(2));
        stack.Add(Info("c"), Seconds(3));

        Assert.Equal(["a", "b", "c"], Titles(stack));
        Assert.Equal(1, stack.WaitingCount);
        Assert.False(error.IsShown);
    }

    [Fact]
    public void A_waiting_sticky_toast_comes_back_at_the_top_when_there_is_room()
    {
        var stack = new NotificationStack();
        stack.Add(Error("e"), Seconds(0));
        stack.Add(Info("a"), Seconds(1));
        stack.Add(Info("b"), Seconds(2));
        stack.Add(Info("c"), Seconds(3));

        // "a" goes at 7: the error is back, above "b" and "c", which keep their places.
        stack.RemoveDue(Seconds(7));

        Assert.Equal(["e", "b", "c"], Titles(stack));
        Assert.Equal(0, stack.WaitingCount);
    }

    [Fact]
    public void A_waiting_toast_that_runs_out_of_time_is_dropped()
    {
        var stack = new NotificationStack();
        stack.Add(Info("a"), Seconds(0));
        stack.Add(Error("e1"), Seconds(1));
        stack.Add(Error("e2"), Seconds(1));
        stack.Add(Error("e3"), Seconds(1));

        Assert.True(stack.RemoveDue(Seconds(6)));
        Assert.Equal(0, stack.WaitingCount);
        Assert.Equal(["e1", "e2", "e3"], Titles(stack));
    }

    [Fact]
    public void A_toast_back_from_waiting_gets_at_least_two_seconds()
    {
        var stack = new NotificationStack();
        var a = stack.Add(Info("a"), Seconds(0));
        var e1 = stack.Add(Error("e1"), Seconds(0));
        stack.Add(Error("e2"), Seconds(0));
        stack.Add(Error("e3"), Seconds(0));

        // Closing an error at 5.5 brings "a" back with half a second left: it stays two seconds.
        stack.Remove(e1, Seconds(5.5));

        Assert.True(a.IsShown);
        Assert.Equal(Seconds(7.5), a.DueAt);
    }

    [Fact]
    public void The_same_message_as_a_waiting_toast_brings_it_back_as_the_newest()
    {
        var stack = new NotificationStack();
        var a = stack.Add(Info("a"), Seconds(0));
        stack.Add(Error("e1"), Seconds(0));
        stack.Add(Error("e2"), Seconds(0));
        stack.Add(Error("e3"), Seconds(0));

        stack.Add(Info("a"), Seconds(2));

        Assert.Equal(["e2", "e3", "a"], Titles(stack));
        Assert.Equal(2, a.Count);
        Assert.Equal(1, stack.WaitingCount);
    }

    [Fact]
    public void A_burst_past_the_waiting_limit_drops_messages_before_sticky_toasts()
    {
        var stack = new NotificationStack();
        var error = stack.Add(Error("e"), Seconds(0));
        for (var i = 0; i < 40; i++)
        {
            stack.Add(Info($"m{i}"), Seconds(0.05 * i));
        }

        Assert.Equal(NotificationStack.MaxShown + NotificationStack.MaxWaiting, stack.Count);
        Assert.Equal(["m37", "m38", "m39"], Titles(stack));

        // The newest go about six seconds after they came, not after every older message had its turn.
        Assert.Equal(8.65, stack.Shown[2].DueAt!.Value.TotalSeconds, 3);

        // The error waits through the burst and is back once the messages have gone.
        stack.RemoveDue(Seconds(60));
        Assert.Equal(["e"], Titles(stack));
        Assert.True(error.IsShown);
    }

    [Fact]
    public void Past_the_limit_with_only_sticky_toasts_waiting_the_oldest_is_dropped()
    {
        var stack = new NotificationStack();
        for (var i = 0; i < NotificationStack.MaxShown + NotificationStack.MaxWaiting + 1; i++)
        {
            stack.Add(Error($"e{i}"), Seconds(i));
        }

        Assert.Equal(NotificationStack.MaxShown + NotificationStack.MaxWaiting, stack.Count);
        stack.RemoveDue(Seconds(1000));
        Assert.Equal(NotificationStack.MaxShown + NotificationStack.MaxWaiting, stack.Count);
    }

    // ===== Holding =====

    [Fact]
    public void Holding_the_stack_stops_every_timer()
    {
        var stack = new NotificationStack();
        var a = stack.Add(Info("a"), Seconds(0));
        stack.Hold(Seconds(1));

        Assert.True(stack.IsHeld);
        Assert.Null(stack.NextDue);
        Assert.False(stack.RemoveDue(Seconds(60)));
        Assert.Null(a.DueAt);
    }

    [Fact]
    public void Letting_go_gives_back_the_time_that_was_left()
    {
        var stack = new NotificationStack();
        var a = stack.Add(Info("a"), Seconds(0));
        stack.Hold(Seconds(2));
        stack.Release(Seconds(30));

        Assert.Equal(Seconds(34), a.DueAt);
    }

    [Fact]
    public void Letting_go_leaves_at_least_two_seconds_still_in_order()
    {
        var stack = new NotificationStack();
        var a = stack.Add(Info("a"), Seconds(0));
        var b = stack.Add(Info("b"), Seconds(0));
        stack.Hold(Seconds(5.9));
        stack.Release(Seconds(20));

        Assert.Equal(Seconds(22), a.DueAt);
        Assert.Equal(Seconds(22.4), b.DueAt);
    }

    [Fact]
    public void A_toast_added_while_held_waits_too_and_gets_its_full_time_afterwards()
    {
        var stack = new NotificationStack();
        stack.Hold(Seconds(0));
        var a = stack.Add(Info("a"), Seconds(1));

        Assert.Null(a.DueAt);
        stack.Release(Seconds(10));
        Assert.Equal(Seconds(16), a.DueAt);
    }
}
