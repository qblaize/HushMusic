using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

/// <summary>settings.json: what JsonSettingsService writes and reads back.</summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hushmusic-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public SettingsServiceTests()
    {
        _paths = new AppPaths(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task The_file_keeps_the_format_of_earlier_versions()
    {
        var service = new JsonSettingsService(_paths, NullLogger<JsonSettingsService>.Instance);
        await service.UpdateAsync(
            s =>
            {
                s.Volume = 0.35;
                s.Theme = "Light";
                s.CloseToTray = true;
                s.GlobalHotkeys["PlayPause"] = "Ctrl+Alt+P";
                s.RecentSearches.Add("daft punk");
            },
            Ct);

        // Earlier versions wrote the file with reflection-based System.Text.Json and these options.
        var expected = JsonSerializer.Serialize(service.Current, new JsonSerializerOptions { WriteIndented = true });
        Assert.Equal(expected, await File.ReadAllTextAsync(_paths.SettingsFile, Ct));
    }

    [Fact]
    public async Task Missing_and_unknown_values_keep_the_defaults()
    {
        await File.WriteAllTextAsync(_paths.SettingsFile, """{ "Volume": 0, "Theme": "Light", "NoLongerUsed": true }""", Ct);
        var service = new JsonSettingsService(_paths, NullLogger<JsonSettingsService>.Instance);

        await service.LoadAsync(Ct);

        Assert.Equal(0, service.Current.Volume);
        Assert.Equal("Light", service.Current.Theme);
        Assert.Equal(new AppSettings().AccentStyle, service.Current.AccentStyle);
        Assert.True(service.Current.ResumeLastSession);
    }

    [Fact]
    public async Task An_unreadable_file_gives_the_defaults()
    {
        await File.WriteAllTextAsync(_paths.SettingsFile, "{ not json", Ct);
        var service = new JsonSettingsService(_paths, NullLogger<JsonSettingsService>.Instance);

        await service.LoadAsync(Ct);

        Assert.Equal(new AppSettings().Volume, service.Current.Volume);
    }
}
