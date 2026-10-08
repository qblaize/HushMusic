using System.Text.Json;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Services;

public sealed class JsonSettingsService(IAppPaths paths, ILogger<JsonSettingsService> logger) : ISettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.SettingsFile))
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(paths.SettingsFile);
            Current = await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            logger.LogWarning(ex, "Settings file is unreadable, using defaults");
            Current = new AppSettings();
        }
    }

    public async Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            update(Current);
            var temp = paths.SettingsFile + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, Current, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, paths.SettingsFile, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
