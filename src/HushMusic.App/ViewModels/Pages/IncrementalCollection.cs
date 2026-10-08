using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>
/// A list that grows page by page through an InnerTube continuation token. ListView/GridView call
/// <see cref="LoadMoreItemsAsync"/> by themselves when the user scrolls near the end; other callers use
/// <see cref="LoadMoreAsync"/>. Must be used on the UI thread.
/// </summary>
public sealed partial class IncrementalCollection<T> : ObservableCollection<T>, ISupportIncrementalLoading
{
    private static readonly PropertyChangedEventArgs IsLoadingArgs = new(nameof(IsLoading));
    private static readonly PropertyChangedEventArgs HasMoreItemsArgs = new(nameof(HasMoreItems));
    private static readonly PropertyChangedEventArgs CountArgs = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerArgs = new("Item[]");

    private readonly Func<string, CancellationToken, Task<Paged<T>>> _fetch;
    private readonly Action<Exception> _onError;
    private readonly Func<CancellationToken> _lifetime;
    private string? _continuation;
    private int _version;
    private bool _isLoading;
    private bool _failed;

    /// <param name="fetch">Loads the page for a continuation token.</param>
    /// <param name="onError">Called when a page fails to load; loading then stops until the next <see cref="Reset"/>.</param>
    /// <param name="lifetime">Token that cancels loads in flight, typically the page's navigation token.</param>
    public IncrementalCollection(Func<string, CancellationToken, Task<Paged<T>>> fetch, Action<Exception> onError, Func<CancellationToken> lifetime)
    {
        _fetch = fetch;
        _onError = onError;
        _lifetime = lifetime;
    }

    public bool HasMoreItems => _continuation is not null && !_failed;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                OnPropertyChanged(IsLoadingArgs);
            }
        }
    }

    /// <summary>Replaces the contents with a first page and its continuation.</summary>
    public void Reset(IEnumerable<T> items, string? continuation)
    {
        _version++;
        _continuation = continuation;
        _failed = false;
        IsLoading = false;

        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(CountArgs);
        OnPropertyChanged(IndexerArgs);
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        OnPropertyChanged(HasMoreItemsArgs);
    }

    public Task<int> LoadMoreAsync() => LoadMoreCoreAsync(CancellationToken.None);

    public IAsyncOperation<LoadMoreItemsResult> LoadMoreItemsAsync(uint count) =>
        AsyncInfo.Run(async ct => new LoadMoreItemsResult { Count = (uint)await LoadMoreCoreAsync(ct) });

    private async Task<int> LoadMoreCoreAsync(CancellationToken cancellationToken)
    {
        if (_isLoading || _failed || _continuation is not { } continuation)
        {
            return 0;
        }

        var version = _version;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime(), cancellationToken);
        IsLoading = true;
        try
        {
            var page = await _fetch(continuation, linked.Token);
            if (version != _version)
            {
                return 0;
            }

            foreach (var item in page.Items)
            {
                Add(item);
            }

            _continuation = page.Continuation;
            return page.Items.Count;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception ex)
        {
            if (version == _version)
            {
                _failed = true;
                _onError(ex);
            }

            return 0;
        }
        finally
        {
            if (version == _version)
            {
                IsLoading = false;
                OnPropertyChanged(HasMoreItemsArgs);
            }
        }
    }
}
