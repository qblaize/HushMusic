using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Devices.Enumeration;
using Windows.Media.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Playback;

// The audio output both players use (AppSettings.AudioOutputDeviceId). Null follows the Windows default output, which
// MediaPlayer does by itself. A chosen output is watched while it is chosen: when it isn't connected, the players use
// the system default, and they move back to it as soon as it is connected again. The choice itself never changes.
public sealed partial class MediaPlayerService
{
    // Taken before _gate, never inside it.
    private readonly object _outputGate = new();

    // Guarded by _outputGate.
    private AudioOutputWatcher? _outputs; // runs while a specific output is chosen
    private string? _outputChoice;        // last seen AppSettings.AudioOutputDeviceId
    private string? _outputInUse;         // the chosen output while it is connected; null = the system default
    private bool _outputMissing;          // the chosen output isn't connected (logged once)
    private bool _outputClosed;

    // Guarded by _gate: the output the players should use; null = the system default. Each Deck knows the one it uses.
    private DeviceInformation? _outputDevice;

    // Settings can change on the UI thread: the watcher and the players are handled on the thread pool. The setting is
    // read when the work runs, so quick successive changes end on the last one.
    private void OnOutputSettingChanged()
    {
        if (!AudioOutputChoice.SameDevice(_settings.Current.AudioOutputDeviceId, Volatile.Read(ref _outputChoice)))
        {
            _ = Task.Run(UpdateOutputChoice);
        }
    }

    private void UpdateOutputChoice()
    {
        var choice = AudioOutputChoice.Normalize(_settings.Current.AudioOutputDeviceId);
        AudioOutputWatcher? stopped = null;
        lock (_outputGate)
        {
            if (_outputClosed || AudioOutputChoice.SameDevice(choice, _outputChoice))
            {
                return;
            }

            _outputChoice = choice;
            _outputMissing = false;
            if (choice is null)
            {
                stopped = _outputs;
                _outputs = null;
            }
            else if (_outputs is null)
            {
                var outputs = new AudioOutputWatcher(_logger);
                outputs.Changed += OnOutputsChanged;
                _outputs = outputs;
                try
                {
                    outputs.Start();
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
                {
                    // Never listed, so never switched: the players stay on the system default.
                    _logger.LogWarning(ex, "Couldn't watch the audio outputs; playing on the system default");
                }
            }

            RefreshOutputNoLock();
        }

        if (stopped is not null)
        {
            stopped.Changed -= OnOutputsChanged;
            stopped.Dispose();
        }
    }

    private void OnOutputsChanged(object? sender, EventArgs e)
    {
        lock (_outputGate)
        {
            if (!_outputClosed && sender == _outputs)
            {
                RefreshOutputNoLock();
            }
        }
    }

    // Under _outputGate.
    private void RefreshOutputNoLock()
    {
        var choice = _outputChoice;
        var outputs = _outputs;
        var id = outputs is null ? null : AudioOutputChoice.Effective(choice, outputs.Devices.Select(d => d.Id));
        var device = id is null ? null : outputs!.Find(id);
        if (choice is not null && device is null && outputs?.IsEnumerated != true)
        {
            return; // still listing the outputs: the chosen one may come next
        }

        var name = choice is null ? null : outputs?.NameOf(choice) ?? choice;
        if (AudioOutputChoice.SameDevice(device?.Id, _outputInUse))
        {
            if (choice is not null && device is null && !_outputMissing)
            {
                _outputMissing = true;
                _logger.LogInformation("Audio output {Device} isn't connected; playing on the system default until it's connected", name);
            }

            return;
        }

        var lost = choice is not null && AudioOutputChoice.SameDevice(_outputInUse, choice);
        _outputInUse = device?.Id;
        if (device is not null)
        {
            if (_outputMissing)
            {
                _logger.LogInformation("Audio output {Device} is connected again; switching back to it", device.Name);
            }
            else
            {
                _logger.LogInformation("Audio output: {Device}", device.Name);
            }

            _outputMissing = false;
        }
        else if (choice is null)
        {
            _logger.LogInformation("Audio output: system default");
        }
        else if (lost)
        {
            _outputMissing = true;
            _logger.LogInformation("Audio output {Device} was disconnected; playing on the system default until it's back", name);
        }
        else
        {
            _outputMissing = true;
            _logger.LogInformation("Audio output {Device} isn't connected; playing on the system default until it's connected", name);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _outputDevice = device;
        }

        MoveToOutput();
    }

    /// <summary>
    /// Moves both players to <see cref="_outputDevice"/>. Media Foundation can't always move playing media to another
    /// output: depending on the two devices it fails with a decoding error, and going back to the system default always
    /// does. A player without media switches cleanly, so open media is taken off its player, the output is switched and
    /// the same media source is put back: Media Foundation opens it again from what it already has, in milliseconds, and
    /// OnMediaOpened continues where it was. (Opening the URL anew can stall for seconds while the old connection to the
    /// stream server is being closed.) A live station connects again instead, like Play after a pause, and a crossfade's
    /// outgoing track stops.
    /// </summary>
    private void MoveToOutput()
    {
        CancellationTokenSource? dropped = null;
        QueueItem? reload = null;
        var kind = LoadKind.Output;
        var loadId = 0L;
        var autoplay = false;
        var resumeAt = TimeSpan.Zero;
        lock (_gate)
        {
            if (_disposed || (IsOnOutputNoLock(_active) && IsOnOutputNoLock(_spare)))
            {
                return;
            }

            if (_crossfadeStarting)
            {
                // The next item takes over in a moment; move the players once it has.
                _ = Task.Delay(QuickFade).ContinueWith(_ => MoveToOutput(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
                return;
            }

            StopFadeOutNoLock();
            if (_preload is { Source: { } preloadSource } preload)
            {
                preload.Opened = false;
                if (!ReattachNoLock(preload.Deck, preloadSource))
                {
                    dropped = DropPreloadNoLock("it couldn't be moved to the new audio output");
                }
            }
            else
            {
                EnsureOutputNoLock(_spare);
            }

            if (_source is not { } source)
            {
                EnsureOutputNoLock(_active);
            }
            else if (_ended)
            {
                // The queue ended: Play starts the item again from the top, opened on the new output. Otherwise the next
                // item is about to open, and opens there anyway.
                if (_status == PlaybackStatus.Ended)
                {
                    DetachSourceNoLock();
                    _startAt = TimeSpan.Zero;
                    EnsureOutputNoLock(_active);
                }
            }
            else if (_item is { } item)
            {
                autoplay = _autoplay;
                resumeAt = item.Track.IsLiveRadio ? TimeSpan.Zero : _opened ? PositionNoLock() : _startAt;
                loadId = _loadId;
                if (item.Track.IsLiveRadio)
                {
                    reload = item;
                    kind = LoadKind.Reconnect;
                }
                else
                {
                    // Opening again: OnMediaOpened seeks to _startAt and plays when _autoplay is set, as for a new load.
                    _opened = false;
                    _starting = false;
                    _startAt = resumeAt;
                    if (ReattachNoLock(_active, source))
                    {
                        _logger.LogDebug("Moved {VideoId} to the new audio output at {Position}", item.Track.VideoId, resumeAt);
                    }
                    else
                    {
                        reload = item;
                    }
                }
            }
        }

        dropped?.Cancel();
        if (reload is not null)
        {
            _logger.LogDebug("Opening {VideoId} again on the new audio output", reload.Track.VideoId);
            _ = LoadAsync(reload, autoplay, resumeAt, kind, CancellationToken.None, onlyIfLoadId: loadId);
        }
    }

    // Takes the source off the player, switches the output and puts the same source back. False when that failed and
    // the player was left without media.
    private bool ReattachNoLock(Deck deck, MediaSource source)
    {
        try
        {
            deck.Player.Source = null;
            EnsureOutputNoLock(deck);
            deck.Player.Source = source;
            return true;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Couldn't move player {Deck}'s media to the new audio output", deck.Name);
            try
            {
                deck.Player.Source = null;
            }
            catch (Exception inner) when (inner is COMException or ObjectDisposedException)
            {
                _logger.LogDebug(inner, "Clearing player {Deck} failed", deck.Name);
            }

            return false;
        }
    }

    private bool IsOnOutputNoLock(Deck deck) => AudioOutputChoice.SameDevice(deck.OutputId, _outputDevice?.Id);

    // Only for a player without media (see MoveToOutput); every source is set right after this.
    private void EnsureOutputNoLock(Deck deck)
    {
        if (IsOnOutputNoLock(deck))
        {
            return;
        }

        var device = _outputDevice;
        try
        {
            deck.Player.AudioDevice = device;
            deck.OutputId = device?.Id;
            _logger.LogDebug("Player {Deck} now plays on {Device}", deck.Name, device?.Name ?? "the system default");
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or ObjectDisposedException)
        {
            // Typically the output went away a moment ago: the watcher reports it next, and the players move to the default.
            _logger.LogWarning(ex, "Couldn't move player {Deck} to {Device}", deck.Name, device?.Name ?? "the system default");
        }
    }

    private void CloseOutputs()
    {
        AudioOutputWatcher? outputs;
        lock (_outputGate)
        {
            _outputClosed = true;
            outputs = _outputs;
            _outputs = null;
        }

        if (outputs is not null)
        {
            outputs.Changed -= OnOutputsChanged;
            outputs.Dispose();
        }
    }
}
