using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Windows.Storage;
using Windows.Storage.Pickers;
using HushMusic.App.Controls.Items;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.Core.Services;

namespace HushMusic.App.Services.Pages;

/// <summary>"Export…" on playlist, album and Liked songs pages: saves the songs as CSV or M3U (<see cref="PlaylistExport"/>).</summary>
public interface IPlaylistExportService
{
    /// <summary>
    /// Asks where to save (CSV or M3U), loads every song with <paramref name="loadTracks"/> and writes the file. The result
    /// shows as a toast. Never throws.
    /// </summary>
    /// <param name="title">The list's title: the suggested file name.</param>
    /// <param name="noun">"playlist" or "album", for the messages.</param>
    /// <param name="loadTracks">The whole list, all pages loaded.</param>
    /// <param name="busyChanged">True once a file is chosen and the songs load, false when it is done.</param>
    Task ExportAsync(string title, string noun, Func<CancellationToken, Task<IReadOnlyList<Track>>> loadTracks, Action<bool>? busyChanged = null);
}

internal sealed class PlaylistExportService(IAppPaths paths, INotificationService notifications, ILogger<PlaylistExportService> logger) : IPlaylistExportService
{
    // Automated test instances can't use the save dialog (it would open on the desktop): they write to <data>\exports,
    // as CSV, or as M3U with HUSHMUSIC_TEST_EXPORT=m3u8.
    private readonly bool _testInstance = Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_BACKGROUND") == "1";

    public async Task ExportAsync(string title, string noun, Func<CancellationToken, Task<IReadOnlyList<Track>>> loadTracks, Action<bool>? busyChanged = null)
    {
        Target? target;
        try
        {
            target = _testInstance ? TestTarget(title) : await PickTargetAsync(title);
        }
        catch (Exception ex)
        {
            notifications.ShowError("Couldn't open the save dialog", ex);
            return;
        }

        if (target is null)
        {
            return;
        }

        busyChanged?.Invoke(true);
        try
        {
            var tracks = await loadTracks(CancellationToken.None);
            await Task.Run(() => WriteAsync(target, tracks));
            logger.LogInformation("Exported {Count} songs as {Format}", tracks.Count, target.Format);
            notifications.Show(new AppNotification(
                NotificationSeverity.Success,
                noun == "album" ? "Album exported" : "Playlist exported",
                $"{ItemFormat.TrackCount(tracks.Count)} saved to {Path.GetFileName(target.Path)}.")
            {
                Action = string.IsNullOrEmpty(target.Path) ? null : new NotificationAction("Show in folder", () => ShowInFolder(target.Path)),
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Exporting a {Noun} failed", noun);
            notifications.ShowError($"Couldn't export the {noun}", ex);
        }
        finally
        {
            busyChanged?.Invoke(false);
        }
    }

    private static async Task<Target?> PickTargetAsync(string title)
    {
        if (App.MainWindow is not { } window)
        {
            return null;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = PlaylistExport.FileNameFor(title),
        };
        picker.FileTypeChoices.Add("CSV spreadsheet", [PlaylistExport.ExtensionOf(PlaylistExportFormat.Csv)]);
        picker.FileTypeChoices.Add("M3U playlist", [PlaylistExport.ExtensionOf(PlaylistExportFormat.M3u)]);

        // An unpackaged app has no CoreWindow: the picker needs the window that owns it.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(window));
        var file = await picker.PickSaveFileAsync();
        return file is null ? null : new Target(file.Path, PlaylistExport.FormatOf(file.Name) ?? PlaylistExportFormat.Csv, file);
    }

    private Target TestTarget(string title)
    {
        var format = string.Equals(Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_EXPORT"), "m3u8", StringComparison.OrdinalIgnoreCase)
            ? PlaylistExportFormat.M3u
            : PlaylistExportFormat.Csv;
        var folder = Directory.CreateDirectory(Path.Combine(paths.Root, "exports")).FullName;
        return new Target(Path.Combine(folder, PlaylistExport.FileNameFor(title) + PlaylistExport.ExtensionOf(format)), format, null);
    }

    private static async Task WriteAsync(Target target, IReadOnlyList<Track> tracks)
    {
        if (!string.IsNullOrEmpty(target.Path))
        {
            await using var stream = new FileStream(target.Path, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
            await PlaylistExport.WriteAsync(stream, target.Format, tracks);
            return;
        }

        // A location without a file system path (a library or a cloud provider): through the storage API.
        await using var storage = await target.File!.OpenStreamForWriteAsync();
        storage.SetLength(0);
        await PlaylistExport.WriteAsync(storage, target.Format, tracks);
    }

    private void ShowInFolder(string path)
    {
        try
        {
            // Explorer wants "/select," and the quoted path as one argument.
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not open the folder of an exported list");
        }
    }

    private sealed record Target(string Path, PlaylistExportFormat Format, StorageFile? File);
}
