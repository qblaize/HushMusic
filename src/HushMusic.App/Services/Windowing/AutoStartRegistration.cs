using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Services;

namespace HushMusic.App.Services.Windowing;

/// <summary>
/// Mirrors <see cref="AppSettings.StartWithWindows"/> to HKCU\...\Run (value "HushMusic" = "&lt;exe&gt;" --background),
/// at startup (which also fixes an entry left by a moved copy) and whenever the setting changes.
/// Unpackaged only: an MSIX install would need a StartupTask in the manifest instead.
/// </summary>
internal sealed class AutoStartRegistration : IDisposable
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultValueName = "HushMusic";

    private readonly ISettingsService _settings;
    private readonly ILogger _logger;
    private readonly string? _valueName;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AutoStartRegistration(ISettingsService settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;

        // Test instances must never touch the real entry: they only get one under an explicit test name.
        var overrideName = Environment.GetEnvironmentVariable("HUSHMUSIC_RUN_VALUE_NAME");
        var testInstance = Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_BACKGROUND") == "1";
        _valueName = overrideName is { Length: > 0 } ? overrideName : testInstance ? null : DefaultValueName;
    }

    public void Start()
    {
        if (_valueName is null)
        {
            _logger.LogDebug("Start with Windows: skipped in a test instance without HUSHMUSIC_RUN_VALUE_NAME");
            return;
        }

        if (Win32.IsPackaged())
        {
            _logger.LogInformation("Start with Windows: packaged install, the Run key is not used");
            return;
        }

        _settings.Changed += OnSettingsChanged;
        _ = ReconcileAsync();
    }

    public void Dispose() => _settings.Changed -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, EventArgs e) => _ = ReconcileAsync();

    private async Task ReconcileAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(Reconcile).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not update the start-with-Windows entry");
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Reconcile()
    {
        var executable = Environment.ProcessPath;
        if (_valueName is null || string.IsNullOrEmpty(executable))
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        var current = key.GetValue(_valueName) as string;
        switch (AutoStartCommand.Reconcile(_settings.Current.StartWithWindows, current, executable))
        {
            case AutoStartAction.Write:
                key.SetValue(_valueName, AutoStartCommand.Build(executable), RegistryValueKind.String);
                _logger.LogInformation("Start with Windows: registered {Executable} as {Name}", executable, _valueName);
                break;
            case AutoStartAction.Delete:
                key.DeleteValue(_valueName, throwOnMissingValue: false);
                _logger.LogInformation("Start with Windows: removed {Name}", _valueName);
                break;
        }
    }
}
