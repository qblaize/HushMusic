using Windows.ApplicationModel.DataTransfer;
using HushMusic.Core.Models;

namespace HushMusic.App.Helpers;

/// <summary>
/// Drag and drop of tracks inside the app (rows and cards → queue, player bar, playlists). The package carries only a
/// token; the tracks stay in this process.
/// </summary>
public static class TrackDragData
{
    public const string Format = "HushMusic.Tracks";

    private static readonly Lock Gate = new();
    private static string? _token;
    private static IReadOnlyList<Track> _tracks = [];

    /// <summary>Call from a DragStarting handler. <paramref name="caption"/> is shown under the cursor.</summary>
    public static void Set(DataPackage package, IReadOnlyList<Track> tracks, string caption)
    {
        ArgumentNullException.ThrowIfNull(package);
        var token = Guid.NewGuid().ToString("N");
        lock (Gate)
        {
            _token = token;
            _tracks = tracks;
        }

        package.SetData(Format, token);
        package.RequestedOperation = DataPackageOperation.Copy;
        package.Properties.Title = caption;
    }

    /// <summary>True when the drag carries tracks (use in DragOver to accept the drop).</summary>
    public static bool Has(DataPackageView view) => view.Contains(Format);

    /// <summary>The dragged tracks, or null when the package is not ours or has expired.</summary>
    public static async Task<IReadOnlyList<Track>?> TryGetAsync(DataPackageView view)
    {
        if (!view.Contains(Format))
        {
            return null;
        }

        var token = await view.GetDataAsync(Format) as string;
        lock (Gate)
        {
            return token is not null && token == _token ? _tracks : null;
        }
    }
}
