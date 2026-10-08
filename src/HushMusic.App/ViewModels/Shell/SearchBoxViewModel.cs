using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Shell;

/// <summary>
/// The search modal (rail Search button / Ctrl+F): open state, debounced suggestions, keyboard highlight and what
/// happens when a query or a suggestion is chosen. With an empty field it lists the recent searches. Singleton.
/// </summary>
public sealed partial class SearchBoxViewModel : ObservableObject
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    // Recent searches that match the typed text, listed above YouTube Music's suggestions.
    private const int MaxMatchingRecent = 3;

    private readonly ISearchApi _search;
    private readonly IPlaybackActions _playback;
    private readonly INavigationService _navigation;
    private readonly INotificationService _notifications;
    private readonly IRecentSearches _recent;
    private readonly ILogger<SearchBoxViewModel> _logger;
    private CancellationTokenSource? _cts;
    private bool _failureReported;

    public SearchBoxViewModel(
        ISearchApi search,
        IPlaybackActions playback,
        INavigationService navigation,
        INotificationService notifications,
        IRecentSearches recent,
        ILogger<SearchBoxViewModel> logger)
    {
        _search = search;
        _playback = playback;
        _navigation = navigation;
        _notifications = notifications;
        _recent = recent;
        _logger = logger;
        Suggestions.CollectionChanged += OnSuggestionsChanged;
        _recent.Changed += OnRecentChanged;
    }

    /// <summary>Open was requested while already open: the view re-focuses the field and selects its text.</summary>
    public event EventHandler? FocusRequested;

    public ObservableCollection<SearchSuggestionViewModel> Suggestions { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    /// <summary>The text in the search field (kept between openings).</summary>
    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    /// <summary>Keyboard-highlighted suggestion, or -1 when the field itself is "highlighted".</summary>
    [ObservableProperty]
    public partial int HighlightedIndex { get; set; } = -1;

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>The list shows the recent searches (empty field): the "Recent searches" heading and "Clear" are visible.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRecentHeader))]
    public partial bool IsShowingRecent { get; set; }

    public bool ShowRecentHeader => IsShowingRecent && HasSuggestions;

    public void Open()
    {
        if (IsOpen)
        {
            FocusRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        IsOpen = true;
        if (string.IsNullOrWhiteSpace(Query))
        {
            ShowRecent();
        }
        else if (Suggestions.Count == 0)
        {
            Load(Query);
        }
    }

    public void Close()
    {
        _cts?.Cancel();
        _cts = null;
        HighlightedIndex = -1;
        IsOpen = false;
    }

    /// <summary>The user typed (not a programmatic text change).</summary>
    public void OnUserInput(string text)
    {
        Query = text;
        HighlightedIndex = -1;
        Load(text);
    }

    /// <summary>Up (-1) / Down (+1). Moving up from the first suggestion returns to the field.</summary>
    public void MoveHighlight(int delta)
    {
        if (Suggestions.Count == 0)
        {
            HighlightedIndex = -1;
            return;
        }

        HighlightedIndex = Math.Clamp(HighlightedIndex + delta, -1, Suggestions.Count - 1);
    }

    /// <summary>Enter: opens the highlighted suggestion, or searches for the typed text.</summary>
    public void Activate()
    {
        var index = HighlightedIndex;
        Submit(Query, index >= 0 && index < Suggestions.Count ? Suggestions[index] : null);
    }

    /// <summary>A suggestion was clicked.</summary>
    public void Choose(SearchSuggestionViewModel suggestion) => Submit(Query, suggestion);

    /// <summary>The × of a recent search, or Delete on the highlighted one.</summary>
    public void RemoveRecent(SearchSuggestionViewModel? suggestion)
    {
        if (suggestion is { IsRecent: true })
        {
            _recent.Remove(suggestion.Text);
        }
    }

    /// <summary>Delete key: removes the highlighted recent search. Returns false when nothing was removed.</summary>
    public bool RemoveHighlightedRecent()
    {
        var index = HighlightedIndex;
        if (index < 0 || index >= Suggestions.Count || !Suggestions[index].IsRecent)
        {
            return false;
        }

        RemoveRecent(Suggestions[index]);
        return true;
    }

    public void ClearRecent() => _recent.Clear();

    private void Load(string text)
    {
        _cts?.Cancel();
        if (string.IsNullOrWhiteSpace(text))
        {
            _cts = null;
            ShowRecent();
            return;
        }

        // Until YouTube Music answers: the recent searches that match, or nothing (never the full recent list).
        var matching = MatchingRecent(text.Trim());
        if (matching.Count > 0 || IsShowingRecent)
        {
            Replace(matching);
            IsShowingRecent = false;
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = LoadSuggestionsAsync(text.Trim(), cts.Token);
    }

    private void Submit(string? queryText, SearchSuggestionViewModel? chosen)
    {
        // Picking a song, album, artist or playlist remembers what was typed to find it.
        if (chosen?.Item is not null)
        {
            _recent.Add(queryText);
        }

        switch (chosen?.Item)
        {
            case Album album:
                _navigation.NavigateTo(PageKey.Album, album.BrowseId);
                break;
            case Artist artist:
                _navigation.NavigateTo(PageKey.Artist, artist.BrowseId);
                break;
            case Playlist playlist:
                _navigation.NavigateTo(PageKey.Playlist, playlist.PlaylistId);
                break;
            case Track track:
                _ = PlayAsync(track);
                break;
            default:
                var query = (chosen?.Text ?? queryText)?.Trim();
                if (string.IsNullOrEmpty(query))
                {
                    return;
                }

                Query = query;
                _recent.Add(query);
                _navigation.NavigateTo(PageKey.Search, query);
                break;
        }

        Suggestions.Clear();
        Close();
    }

    private async Task LoadSuggestionsAsync(string input, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Debounce, cancellationToken);
            var result = await _search.GetSuggestionsAsync(input, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            HighlightedIndex = -1;
            IsShowingRecent = false;
            var recent = MatchingRecent(input);
            var suggestions = new List<SearchSuggestionViewModel>(recent);
            foreach (var query in result.Queries)
            {
                if (!recent.Any(r => string.Equals(r.Text, query, StringComparison.OrdinalIgnoreCase)))
                {
                    suggestions.Add(SearchSuggestionViewModel.ForQuery(query));
                }
            }

            foreach (var item in result.Items)
            {
                if (SearchSuggestionViewModel.ForItem(item) is { } suggestion)
                {
                    suggestions.Add(suggestion);
                }
            }

            Replace(suggestions);

            _failureReported = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Search suggestions failed");

            // One notification per failure streak, not one per keystroke.
            if (!_failureReported)
            {
                _failureReported = true;
                _notifications.ShowError("Search suggestions are unavailable", ex);
            }
        }
    }

    private async Task PlayAsync(Track track)
    {
        try
        {
            await _playback.PlayTrackWithUpNextAsync(track);
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Could not play {track.Title}", ex);
        }
    }

    private void ShowRecent()
    {
        HighlightedIndex = -1;
        Replace([.. _recent.Items.Select(SearchSuggestionViewModel.ForRecent)]);
        IsShowingRecent = true;
    }

    private List<SearchSuggestionViewModel> MatchingRecent(string text) =>
        [.. _recent.Items
            .Where(q => q.StartsWith(text, StringComparison.CurrentCultureIgnoreCase))
            .Take(MaxMatchingRecent)
            .Select(SearchSuggestionViewModel.ForRecent)];

    private void Replace(IReadOnlyList<SearchSuggestionViewModel> suggestions)
    {
        Suggestions.Clear();
        foreach (var suggestion in suggestions)
        {
            Suggestions.Add(suggestion);
        }
    }

    // A recent search was added, removed or cleared: refresh the visible recent entries, keeping the highlight in range.
    private void OnRecentChanged(object? sender, EventArgs e)
    {
        if (!IsOpen)
        {
            return;
        }

        var highlighted = HighlightedIndex;
        if (IsShowingRecent)
        {
            Replace([.. _recent.Items.Select(SearchSuggestionViewModel.ForRecent)]);
        }
        else
        {
            for (var i = Suggestions.Count - 1; i >= 0; i--)
            {
                if (Suggestions[i].IsRecent && !_recent.Items.Contains(Suggestions[i].Text, StringComparer.OrdinalIgnoreCase))
                {
                    Suggestions.RemoveAt(i);
                }
            }
        }

        HighlightedIndex = Math.Min(highlighted, Suggestions.Count - 1);
    }

    private void OnSuggestionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasSuggestions));
        OnPropertyChanged(nameof(ShowRecentHeader));
    }
}

public enum SearchSuggestionKind
{
    Query,
    Recent,
    Track,
    Album,
    Artist,
    Playlist,
}

public sealed partial class SearchSuggestionViewModel
{
    private readonly SearchSuggestionKind _kind;

    private SearchSuggestionViewModel(SearchSuggestionKind kind, string text, string? detail, string? thumbnailUrl, MediaItem? item)
    {
        _kind = kind;
        Text = text;
        Detail = detail;
        ThumbnailUrl = thumbnailUrl;
        Item = item;
    }

    public string Text { get; }

    public string? Detail { get; }

    public string? ThumbnailUrl { get; }

    public MediaItem? Item { get; }

    public bool IsQuery => _kind is SearchSuggestionKind.Query or SearchSuggestionKind.Recent;

    /// <summary>A recent search (clock glyph and a remove button).</summary>
    public bool IsRecent => _kind == SearchSuggestionKind.Recent;

    public string RemoveLabel => $"Remove {Text} from recent searches";

    public bool HasThumbnail => !IsQuery && !string.IsNullOrEmpty(ThumbnailUrl);

    public bool HasGlyph => !HasThumbnail;

    public bool IsRound => _kind == SearchSuggestionKind.Artist;

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public string Glyph => _kind switch
    {
        SearchSuggestionKind.Query => "",
        SearchSuggestionKind.Recent => "",
        SearchSuggestionKind.Artist => "",
        SearchSuggestionKind.Playlist => "",
        _ => "",
    };

    public static SearchSuggestionViewModel ForQuery(string query) => new(SearchSuggestionKind.Query, query, null, null, null);

    public static SearchSuggestionViewModel ForRecent(string query) => new(SearchSuggestionKind.Recent, query, null, null, null);

    public static SearchSuggestionViewModel? ForItem(MediaItem item) => item switch
    {
        Track t => new(SearchSuggestionKind.Track, t.Title, Join(t.Type == TrackType.Video ? "Video" : "Song", t.ArtistsText), Thumb(t), t),
        Album a => new(SearchSuggestionKind.Album, a.Title, Join(a.Type switch { AlbumType.Single => "Single", AlbumType.EP => "EP", _ => "Album" }, a.ArtistsText, a.Year), Thumb(a), a),
        Artist r => new(SearchSuggestionKind.Artist, r.Title, Join("Artist", r.Subscribers), Thumb(r), r),
        Playlist p => new(SearchSuggestionKind.Playlist, p.Title, Join("Playlist", p.Author?.Name), Thumb(p), p),
        _ => null,
    };

    // Screen readers and the list's text search read this.
    public override string ToString() => HasDetail ? $"{Text}, {Detail}" : Text;

    private static string? Thumb(MediaItem item) => item.ThumbnailFor(80)?.Url;

    private static string Join(params string?[] parts) =>
        string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
