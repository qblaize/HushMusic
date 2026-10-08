using System.Windows.Input;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>
/// A track in a page's track list. A class (not a record) so that the same song listed twice
/// stays two distinct rows.
/// </summary>
public sealed class TrackItem(Track track, int number)
{
    public Track Track { get; } = track;

    /// <summary>1-based position, shown on album pages.</summary>
    public int Number { get; } = number;

    public static List<TrackItem> From(IEnumerable<Track> tracks, int firstNumber = 1) =>
        [.. tracks.Select((track, index) => new TrackItem(track, firstNumber + index))];

    public static Paged<TrackItem> From(Paged<Track> page, int firstNumber) => new(From(page.Items, firstNumber), page.Continuation);

    /// <summary>An incremental track list whose later pages keep numbering after the rows already loaded.</summary>
    public static IncrementalCollection<TrackItem> CreateList(
        Func<string, CancellationToken, Task<Paged<Track>>> fetch,
        Action<Exception> onError,
        Func<CancellationToken> lifetime)
    {
        IncrementalCollection<TrackItem>? list = null;
        list = new IncrementalCollection<TrackItem>(
            async (continuation, ct) => From(await fetch(continuation, ct), list!.Count + 1),
            onError,
            lifetime);
        return list;
    }
}

/// <summary>A titled group of items for grouped lists (search sections, history days).</summary>
public sealed class MediaGroup(string title, IEnumerable<MediaItem> items) : List<MediaItem>(items)
{
    public string Title { get; } = title;

    /// <summary>"Show all" action for the group, when it has one.</summary>
    public ICommand? ShowAllCommand { get; init; }

    public bool CanShowAll => ShowAllCommand is not null;

    /// <summary>Label of the header's text action; null hides it.</summary>
    public string? ShowAllText => CanShowAll ? "Show all" : null;
}
