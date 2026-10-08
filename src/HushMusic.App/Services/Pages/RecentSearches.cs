using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.Services.Pages;

/// <summary>
/// The search modal's "Recent searches": most recent first, at most <see cref="MaxItems"/>, case-insensitively unique.
/// Stored locally in <see cref="AppSettings.RecentSearches"/>. Use from the UI thread; <see cref="Changed"/> is raised on it.
/// </summary>
public interface IRecentSearches
{
    IReadOnlyList<string> Items { get; }

    event EventHandler? Changed;

    /// <summary>Moves (or adds) <paramref name="query"/> to the top.</summary>
    void Add(string? query);

    void Remove(string query);

    void Clear();
}

internal sealed class RecentSearches : IRecentSearches
{
    public const int MaxItems = 8;

    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<RecentSearches> _logger;
    private List<string>? _items;
    private int _pendingSaves;

    public RecentSearches(ISettingsService settings, IUiDispatcher dispatcher, ILogger<RecentSearches> logger)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;

        // Someone else (e.g. a "clear search history" setting) changed the stored list.
        _settings.Changed += (_, _) => _dispatcher.Run(() =>
        {
            if (_pendingSaves == 0 && _items is not null && !_items.SequenceEqual(_settings.Current.RecentSearches ?? [], StringComparer.Ordinal))
            {
                _items = Normalize(_settings.Current.RecentSearches);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        });
    }

    public event EventHandler? Changed;

    public IReadOnlyList<string> Items => _items ??= Normalize(_settings.Current.RecentSearches);

    public void Add(string? query)
    {
        var text = query?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var items = Items.Where(q => !string.Equals(q, text, StringComparison.OrdinalIgnoreCase)).Prepend(text);
        Save([.. items.Take(MaxItems)]);
    }

    public void Remove(string query) =>
        Save([.. Items.Where(q => !string.Equals(q, query, StringComparison.OrdinalIgnoreCase))]);

    public void Clear() => Save([]);

    internal static List<string> Normalize(IEnumerable<string>? stored)
    {
        var result = new List<string>();
        foreach (var query in stored ?? [])
        {
            var text = query?.Trim();
            if (!string.IsNullOrEmpty(text) && !result.Contains(text, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(text);
            }

            if (result.Count == MaxItems)
            {
                break;
            }
        }

        return result;
    }

    private void Save(List<string> items)
    {
        if (_items is not null && _items.SequenceEqual(items, StringComparer.Ordinal))
        {
            return;
        }

        _items = items;
        Changed?.Invoke(this, EventArgs.Empty);
        _pendingSaves++;
        _ = PersistAsync([.. items]);
    }

    private async Task PersistAsync(List<string> snapshot)
    {
        try
        {
            await _settings.UpdateAsync(s => s.RecentSearches = snapshot);
        }
        catch (Exception ex)
        {
            // Local convenience data: not worth an error banner.
            _logger.LogWarning(ex, "Could not save recent searches");
        }
        finally
        {
            _pendingSaves--;
        }
    }
}
