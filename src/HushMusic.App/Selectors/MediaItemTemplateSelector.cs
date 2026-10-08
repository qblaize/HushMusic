using HushMusic.App.ViewModels.Pages;
using HushMusic.Core.Models;

namespace HushMusic.App.Selectors;

/// <summary>Picks an item template by <see cref="MediaItem"/> subtype (a <see cref="TrackItem"/> counts as a track).</summary>
public sealed partial class MediaItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TrackTemplate { get; set; }

    /// <summary>Music videos; falls back to <see cref="TrackTemplate"/>.</summary>
    public DataTemplate? VideoTemplate { get; set; }

    public DataTemplate? AlbumTemplate { get; set; }

    public DataTemplate? ArtistTemplate { get; set; }

    public DataTemplate? PlaylistTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) => Select(item) ?? base.SelectTemplateCore(item);

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        Select(item) ?? base.SelectTemplateCore(item, container);

    private DataTemplate? Select(object item) => item switch
    {
        TrackItem trackItem => Select(trackItem.Track),
        Track { Type: TrackType.Video } => VideoTemplate ?? TrackTemplate,
        Track => TrackTemplate,
        Album => AlbumTemplate,
        Artist => ArtistTemplate,
        Playlist => PlaylistTemplate,
        _ => null,
    };
}
