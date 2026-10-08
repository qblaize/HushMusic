using CommunityToolkit.Mvvm.ComponentModel;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>
/// An artist section's "See all" (<see cref="Shelf.MoreBrowseId"/> "MPAD..." with <see cref="Shelf.MoreParams"/>).
/// <paramref name="Title"/> is the section's title ("Albums", "Singles &amp; EPs").
/// </summary>
public sealed record ArtistDiscographyRequest(string BrowseId, string? Params, string Title, string? ArtistName);

/// <summary>Every album or single of an artist, as a grid that loads more as it scrolls.</summary>
public sealed partial class ArtistDiscographyViewModel : PageViewModelBase
{
    private readonly IBrowseApi _browse;
    private ArtistDiscographyRequest? _request;

    public ArtistDiscographyViewModel(IBrowseApi browse, PageServices services)
        : base(services)
    {
        _browse = browse;
        Albums = new IncrementalCollection<Album>(
            (continuation, ct) => _browse.GetArtistAlbumsAsync(string.Empty, null, continuation, ct),
            ex => ReportError("Couldn't load more of this discography", ex),
            () => NavigationToken);
    }

    public IncrementalCollection<Album> Albums { get; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ArtistName { get; set; }

    protected override Task OnNavigatedToCoreAsync(object? parameter)
    {
        _request = parameter as ArtistDiscographyRequest;
        Title = _request?.Title ?? string.Empty;
        ArtistName = _request?.ArtistName;
        return LoadAsync();
    }

    protected override Task LoadAsync()
    {
        if (_request is not { } request)
        {
            ErrorMessage = "No artist was selected.";
            return Task.CompletedTask;
        }

        return RunAsync(
            async ct =>
            {
                var page = await _browse.GetArtistAlbumsAsync(request.BrowseId, request.Params, null, ct);
                Albums.Reset(page.Items, page.Continuation);
                HasContent = true;
                IsEmpty = Albums.Count == 0;
            },
            "Couldn't load this discography");
    }
}
