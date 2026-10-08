using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Services.Windowing;

/// <summary>Transport commands from outside the window (taskbar buttons, notification-area menu). Errors become toasts.</summary>
internal sealed class PlaybackRemote(IPlayer player, INotificationService notifications)
{
    public bool HasTrack => player.CurrentTrack is not null;

    public bool IsPlaying => player.Status == PlaybackStatus.Playing;

    public Track? Track => player.CurrentTrack;

    public void PlayPause() => Run(() => player.TogglePlayPauseAsync(), "Playback failed");

    public void Next() => Run(() => player.NextAsync(), "Could not skip to the next song");

    public void Previous() => Run(() => player.PreviousAsync(), "Could not go to the previous song");

    private async void Run(Func<Task> action, string errorTitle)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            notifications.ShowError(errorTitle, ex);
        }
    }
}
