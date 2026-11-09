using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Radio;

namespace HushMusic.Playback;

// Live radio: noticing a stream that hangs without failing (LiveStallWatchdog) and reconnecting it like a drop. One
// timer, created when the first station plays and armed only while a station is connecting, buffering or playing: once
// at the start deadline, then about every FrozenTimeout while it plays (the position ticks feed it in between).
public sealed partial class MediaPlayerService
{
    // Guarded by _gate.
    private readonly LiveStallWatchdog _liveWatchdog = new(TimeProvider.System);
    private Timer? _liveWatchdogTimer;
    private long _liveRecoveryFor; // the load whose loss is being handled, so a stall and a failure aren't counted twice
    private bool _reconnecting;    // the live station lost its stream and is connecting again (StatusChanged reports it)
    private long _liveConnectingSinceMs; // when the current connection started, until its audio runs (for the log)

    // Under _gate, after the status changed: watch a live station while it should be heard.
    private void UpdateLiveWatchNoLock()
    {
        if (_disposed || _item?.Track.IsLiveRadio != true || !_autoplay)
        {
            StopLiveWatchNoLock();
            return;
        }

        switch (_status)
        {
            case PlaybackStatus.Loading or PlaybackStatus.Buffering:
                _liveWatchdog.Wait();
                break;
            case PlaybackStatus.Playing:
                _liveWatchdog.Playing(PositionNoLock());
                break;
            default:
                _liveWatchdog.Stop();
                break;
        }

        ArmLiveWatchNoLock();
    }

    // Under _gate: a new connection to the current live station; it has the full start timeout to play.
    private void WatchNewLiveConnectionNoLock()
    {
        if (_item?.Track.IsLiveRadio == true && _autoplay && !_disposed)
        {
            _liveConnectingSinceMs = Environment.TickCount64;
            _liveWatchdog.Wait(restart: true);
            ArmLiveWatchNoLock();
        }
    }

    private void StopLiveWatchNoLock()
    {
        if (_liveWatchdog.IsWatching)
        {
            _liveWatchdog.Stop();
            _liveWatchdogTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    // Under _gate, from the position ticks and the watchdog timer while a live station plays.
    private void SampleLiveNoLock(TimeSpan position)
    {
        if (_liveWatchdog.Sample(position))
        {
            // Audio is really coming in (Media Foundation reports Playing before the first samples arrive).
            _livePolicy.OnPlaying();
            if (_liveConnectingSinceMs > 0)
            {
                _logger.LogDebug("Live station {Station} is playing, {Elapsed} ms after connecting", _item?.Track.Title, Environment.TickCount64 - _liveConnectingSinceMs);
                _liveConnectingSinceMs = 0;
            }
        }
    }

    private void ArmLiveWatchNoLock()
    {
        if (_disposed)
        {
            return;
        }

        if (_liveWatchdog.DueIn() is { } due)
        {
            _liveWatchdogTimer ??= new Timer(_ => CheckLiveStall());
            _liveWatchdogTimer.Change(due, Timeout.InfiniteTimeSpan);
        }
        else
        {
            _liveWatchdogTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    private void CheckLiveStall()
    {
        QueueItem item;
        long loadId;
        string reason;
        string message;
        lock (_gate)
        {
            if (_disposed || _item is not { Track.IsLiveRadio: true } live || !_liveWatchdog.IsWatching)
            {
                return;
            }

            if (_status == PlaybackStatus.Playing)
            {
                SampleLiveNoLock(PositionNoLock());
            }

            if (_liveWatchdog.Check() is not { } stall)
            {
                ArmLiveWatchNoLock();
                return;
            }

            item = live;
            loadId = _loadId;
            reason = stall == LiveStall.NoAudio
                ? $"sent no audio for {LiveStallWatchdog.StartTimeout.TotalSeconds:0} s ({DescribeLiveConnectionNoLock()})"
                : $"stopped sending audio (playing, but stuck at {PositionNoLock():h\\:mm\\:ss} for {LiveStallWatchdog.FrozenTimeout.TotalSeconds:0} s)";
            message = _livePolicy.HasPlayed ? "The station's stream keeps dropping. Try again in a moment." : "The station isn't sending any audio.";
            StopLiveWatchNoLock();
        }

        RecoverLive(item, loadId, fatal: false, reason, message, null);
    }

    // Under _gate: how far the connection got, for the log.
    private string DescribeLiveConnectionNoLock()
    {
        if (_source is null)
        {
            return "no stream yet";
        }

        if (!_opened)
        {
            return "still opening";
        }

        try
        {
            var session = _active.Player.PlaybackSession;
            return $"{session.PlaybackState}, buffered {session.BufferingProgress:P0}";
        }
        catch (COMException ex)
        {
            _logger.LogDebug(ex, "Couldn't read the live station's playback state");
            return "opened";
        }
    }
}
