using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Services;

public sealed class JsonSettingsService(IAppPaths paths, ILogger<JsonSettingsService> logger) : ISettingsService
{
    // Source-generated: the settings are read before the window opens, and reflection-based metadata is slow to build.
    private static readonly JsonTypeInfo<AppSettings> SettingsJson = SettingsJsonContext.Default.AppSettings;

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
            Current = await JsonSerializer.DeserializeAsync(stream, SettingsJson, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
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
                await JsonSerializer.SerializeAsync(stream, Current, SettingsJson, cancellationToken).ConfigureAwait(false);
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

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
