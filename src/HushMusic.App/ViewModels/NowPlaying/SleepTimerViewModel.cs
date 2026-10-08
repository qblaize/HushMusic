using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.ViewModels.NowPlaying;

public enum SleepTimerChoice
{
    Off,
    Minutes15,
    Minutes30,
    Minutes45,
    Hour,
    EndOfTrack,
}

/// <summary>Sleep timer button and menu (player bar and Now Playing). <see cref="ISleepTimer"/> is the source of truth. Singleton.</summary>
public sealed partial class SleepTimerViewModel : ObservableObject
{
    private static readonly TimeSpan LabelRefresh = TimeSpan.FromSeconds(15);

    private readonly ISleepTimer _timer;
    private readonly IPlayer _player;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private SleepTimerChoice _lastDuration = SleepTimerChoice.Off;
    private DispatcherQueueTimer? _refresh;

    public SleepTimerViewModel(ISleepTimer timer, IPlayer player, INotificationService notifications, IUiDispatcher dispatcher)
    {
        _timer = timer;
        _player = player;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _timer.Changed += (_, _) => _dispatcher.Run(Refresh);
        _dispatcher.Run(Refresh);
    }

    public static IReadOnlyList<SleepTimerChoice> Choices { get; } =
    [
        SleepTimerChoice.Off,
        SleepTimerChoice.Minutes15,
        SleepTimerChoice.Minutes30,
        SleepTimerChoice.Minutes45,
        SleepTimerChoice.Hour,
        SleepTimerChoice.EndOfTrack,
    ];

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>Tooltip: "Sleep timer: 23 min left".</summary>
    [ObservableProperty]
    public partial string Label { get; set; } = "Sleep timer";

    /// <summary>A live station never ends by itself, so "end of track" would never fire.</summary>
    public bool CanStopAtEndOfTrack => !_player.IsLive;

    /// <summary>The option the menu shows as selected.</summary>
    public SleepTimerChoice Current =>
        !_timer.IsActive ? SleepTimerChoice.Off
        : _timer.StopsAtEndOfTrack ? SleepTimerChoice.EndOfTrack
        : _lastDuration;

    public static string ChoiceText(SleepTimerChoice choice) => choice switch
    {
        SleepTimerChoice.Minutes15 => "15 minutes",
        SleepTimerChoice.Minutes30 => "30 minutes",
        SleepTimerChoice.Minutes45 => "45 minutes",
        SleepTimerChoice.Hour => "1 hour",
        SleepTimerChoice.EndOfTrack => "End of track",
        _ => "Off",
    };

    public void Choose(SleepTimerChoice choice)
    {
        try
        {
            switch (choice)
            {
                case SleepTimerChoice.Off:
                    _timer.Cancel();
                    break;
                case SleepTimerChoice.EndOfTrack:
                    _timer.StopAtEndOfTrack();
                    break;
                default:
                    _lastDuration = choice;
                    _timer.Start(Duration(choice));
                    break;
            }
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Could not set the sleep timer", ex);
        }

        Refresh();
    }

    private static TimeSpan Duration(SleepTimerChoice choice) => choice switch
    {
        SleepTimerChoice.Minutes15 => TimeSpan.FromMinutes(15),
        SleepTimerChoice.Minutes30 => TimeSpan.FromMinutes(30),
        SleepTimerChoice.Minutes45 => TimeSpan.FromMinutes(45),
        _ => TimeSpan.FromHours(1),
    };

    private void Refresh()
    {
        IsActive = _timer.IsActive;
        Label = !_timer.IsActive ? "Sleep timer"
            : _timer.StopsAtEndOfTrack ? "Sleep timer: stops at the end of this song"
            : _timer.EndsAt is { } endsAt ? $"Sleep timer: {Remaining(endsAt - DateTimeOffset.Now)} left"
            : "Sleep timer: on";
        OnPropertyChanged(nameof(Current));

        // The remaining time in the tooltip ticks down while a timed stop is running.
        var countdown = _timer.IsActive && _timer.EndsAt is not null;
        if (countdown && _refresh is null && DispatcherQueue.GetForCurrentThread() is { } queue)
        {
            _refresh = queue.CreateTimer();
            _refresh.Interval = LabelRefresh;
            _refresh.Tick += (_, _) => Refresh();
        }

        if (countdown)
        {
            _refresh?.Start();
        }
        else
        {
            _refresh?.Stop();
        }
    }

    private static string Remaining(TimeSpan left)
    {
        var minutes = (int)Math.Ceiling(Math.Max(0, left.TotalMinutes));
        return minutes switch
        {
            <= 1 => "1 min",
            < 60 => string.Format(CultureInfo.CurrentCulture, "{0} min", minutes),
            _ => string.Format(CultureInfo.CurrentCulture, "{0} h {1:00} min", minutes / 60, minutes % 60),
        };
    }
}
