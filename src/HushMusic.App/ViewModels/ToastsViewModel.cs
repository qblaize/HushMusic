using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation.Peers;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Services;

namespace HushMusic.App.ViewModels;

/// <summary>
/// The floating toasts. <see cref="NotificationStack"/> decides which are on screen and when each goes; this keeps
/// <see cref="Items"/> in step with it and runs one timer, only while a toast is waiting to go by itself. UI thread only.
/// </summary>
public sealed class ToastsViewModel
{
    // A little past the due time, so the tick never lands just before it.
    private static readonly TimeSpan TimerSlack = TimeSpan.FromMilliseconds(15);

    private readonly NotificationStack _stack = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DispatcherQueueTimer? _timer;

    /// <summary>On screen, oldest (top) first.</summary>
    public ObservableCollection<NotificationViewModel> Items { get; } = [];

    public void Add(AppNotification notification)
    {
        _stack.Add(notification, _clock.Elapsed);
        Sync();
    }

    /// <summary>The pointer rests on the toasts (true) or left them: their time stops meanwhile.</summary>
    public void SetHeld(bool held)
    {
        if (held)
        {
            _stack.Hold(_clock.Elapsed);
        }
        else
        {
            _stack.Release(_clock.Elapsed);
        }

        Schedule();
    }

    private void Dismiss(NotificationViewModel toast)
    {
        if (_stack.Remove(toast.Entry, _clock.Elapsed))
        {
            Sync();
        }
    }

    private void OnTimerTick(DispatcherQueueTimer sender, object args)
    {
        _stack.RemoveDue(_clock.Elapsed);
        Sync();
    }

    // Removes the toasts that went, inserts the ones that came (a toast back from waiting goes in at the top) and updates
    // the counts. Toasts on screen never change order, so this is only removals and insertions.
    private void Sync()
    {
        var shown = _stack.Shown;
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!Contains(shown, Items[i].Entry))
            {
                Items.RemoveAt(i);
            }
        }

        for (var i = 0; i < shown.Count; i++)
        {
            if (i < Items.Count && ReferenceEquals(Items[i].Entry, shown[i]))
            {
                Items[i].Refresh();
            }
            else
            {
                Items.Insert(i, new NotificationViewModel(shown[i], Dismiss));
            }
        }

        Schedule();
    }

    private void Schedule()
    {
        if (_stack.NextDue is not { } due)
        {
            _timer?.Stop();
            return;
        }

        if (_timer is null)
        {
            _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
            _timer.IsRepeating = false;
            _timer.Tick += OnTimerTick;
        }

        var wait = due - _clock.Elapsed;
        _timer.Stop();
        _timer.Interval = (wait > TimeSpan.Zero ? wait : TimeSpan.Zero) + TimerSlack;
        _timer.Start();
    }

    private static bool Contains(IReadOnlyList<StackedNotification> shown, StackedNotification entry)
    {
        foreach (var item in shown)
        {
            if (ReferenceEquals(item, entry))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>One floating toast. Errors and toasts with an action stay until closed; everything else dismisses itself.</summary>
public sealed partial class NotificationViewModel : ObservableObject
{
    private readonly AppNotification _notification;
    private readonly Action<NotificationViewModel> _onDismiss;

    internal NotificationViewModel(StackedNotification entry, Action<NotificationViewModel> onDismiss)
    {
        Entry = entry;
        _notification = entry.Notification;
        _onDismiss = onDismiss;
        Count = entry.Count;
    }

    internal StackedNotification Entry { get; }

    public string Title => _notification.Title;

    public string Message => _notification.Message;

    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

    public NotificationSeverity Severity => _notification.Severity;

    public string Glyph => _notification.Severity switch
    {
        NotificationSeverity.Error => "",
        NotificationSeverity.Warning => "",
        NotificationSeverity.Success => "",
        _ => "",
    };

    /// <summary>Errors interrupt the screen reader; the rest wait their turn.</summary>
    public AutomationLiveSetting LiveSetting =>
        _notification.Severity == NotificationSeverity.Error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite;

    public bool HasAction => _notification.Action is not null;

    public string ActionLabel => _notification.Action?.Label ?? string.Empty;

    public bool HasSecondaryAction => _notification.SecondaryAction is not null;

    public string SecondaryActionLabel => _notification.SecondaryAction?.Label ?? string.Empty;

    /// <summary>How many times the same message came in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCount), nameof(CountText), nameof(CountDescription))]
    public partial int Count { get; private set; }

    public bool HasCount => Count > 1;

    public string CountText => "×" + Count.ToString(CultureInfo.CurrentCulture);

    public string CountDescription => Count.ToString(CultureInfo.CurrentCulture) + " times";

    public void Dismiss() => _onDismiss(this);

    public void InvokeAction()
    {
        Dismiss();
        _notification.Action?.Invoke();
    }

    public void InvokeSecondaryAction()
    {
        Dismiss();
        _notification.SecondaryAction?.Invoke();
    }

    internal void Refresh() => Count = Entry.Count;
}
