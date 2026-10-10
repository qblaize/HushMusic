using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace HushMusic.Playback;

/// <summary>
/// The enabled audio outputs, kept current by a <see cref="DeviceWatcher"/> on the audio render selector: outputs
/// that are plugged in, unplugged, enabled, disabled or renamed while it runs. Thread-safe. <see cref="Changed"/> is
/// raised on a background thread.
/// </summary>
public sealed class AudioOutputWatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, DeviceInformation> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger _logger;
    private DeviceWatcher? _watcher;
    private bool _enumerated;
    private bool _disposed;

    public AudioOutputWatcher(ILogger logger) => _logger = logger;

    /// <summary>Raised after each change to the list, and once when the first full list is in.</summary>
    public event EventHandler? Changed;

    /// <summary>The first full list is in. Until then, an output missing from <see cref="Devices"/> may just not be listed yet.</summary>
    public bool IsEnumerated
    {
        get
        {
            lock (_gate)
            {
                return _enumerated;
            }
        }
    }

    /// <summary>The connected outputs, in no particular order.</summary>
    public IReadOnlyList<AudioOutputDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return [.. _devices.Values.Select(d => new AudioOutputDevice(d.Id, d.Name))];
            }
        }
    }

    /// <summary>
    /// The name Windows has for an output, also one that isn't connected; null when it doesn't know the output (any
    /// more). Doesn't need a running watcher.
    /// </summary>
    public static async Task<string?> FindNameAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        try
        {
            var device = await DeviceInformation.CreateFromIdAsync(deviceId).AsTask(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(device.Name) ? null : device.Name;
        }
        catch (Exception ex) when (ex is FileNotFoundException or COMException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The connected output with this id, ready for <c>MediaPlayer.AudioDevice</c>; null when it isn't connected.</summary>
    public DeviceInformation? Find(string deviceId)
    {
        lock (_gate)
        {
            return _devices.GetValueOrDefault(deviceId);
        }
    }

    /// <summary>The last name seen for an output, also after it was disconnected; null for one not seen since <see cref="Start"/>.</summary>
    public string? NameOf(string deviceId)
    {
        lock (_gate)
        {
            return _names.GetValueOrDefault(deviceId);
        }
    }

    /// <summary>Starts listing and watching. Returns at once; the outputs arrive through <see cref="Changed"/>.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _watcher is not null)
            {
                return;
            }

            var watcher = DeviceInformation.CreateWatcher(MediaDevice.GetAudioRenderSelector());
            watcher.Added += OnAdded;
            watcher.Updated += OnUpdated;
            watcher.Removed += OnRemoved;
            watcher.EnumerationCompleted += OnEnumerationCompleted;
            watcher.Stopped += OnStopped;
            _watcher = watcher;
            watcher.Start();
        }
    }

    public void Dispose()
    {
        DeviceWatcher? watcher;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            watcher = _watcher;
            _watcher = null;
            _devices.Clear();
        }

        if (watcher is null)
        {
            return;
        }

        watcher.Added -= OnAdded;
        watcher.Updated -= OnUpdated;
        watcher.Removed -= OnRemoved;
        watcher.EnumerationCompleted -= OnEnumerationCompleted;
        watcher.Stopped -= OnStopped;
        try
        {
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
            {
                watcher.Stop();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Stopping the audio output watcher failed");
        }
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation device)
    {
        bool enumerated;
        lock (_gate)
        {
            if (sender != _watcher)
            {
                return;
            }

            _names[device.Id] = device.Name;
            if (device.IsEnabled)
            {
                _devices[device.Id] = device;
            }

            enumerated = _enumerated;
        }

        // The first listing adds every output; only later arrivals are news.
        if (enumerated)
        {
            _logger.LogDebug("Audio output connected: {Device}", device.Name);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_gate)
        {
            if (sender != _watcher || !_devices.TryGetValue(update.Id, out var device))
            {
                return;
            }

            device.Update(update);
            _names[device.Id] = device.Name;
            if (!device.IsEnabled)
            {
                _devices.Remove(device.Id);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update)
    {
        lock (_gate)
        {
            if (sender != _watcher || !_devices.Remove(update.Id))
            {
                return;
            }
        }

        _logger.LogDebug("Audio output disconnected: {Device}", NameOf(update.Id));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args)
    {
        lock (_gate)
        {
            if (sender != _watcher)
            {
                return;
            }

            _enumerated = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnStopped(DeviceWatcher sender, object args)
    {
        // Stop() unsubscribes first, so this is Windows ending the watch (e.g. the audio service restarting).
        if (sender.Status == DeviceWatcherStatus.Aborted)
        {
            _logger.LogWarning("Windows stopped reporting audio output changes");
        }
    }
}
