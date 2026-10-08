using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

public sealed partial class PlaylistsViewModel : SignedInPageViewModelBase
{
    private readonly ILibraryApi _library;
    private readonly IAccountActionsService _account;
    private readonly IPlaylistDialogService _dialogs;

    public PlaylistsViewModel(ILibraryApi library, IAccountActionsService account, IPlaylistDialogService dialogs, PageServices services)
        : base(services)
    {
        _library = library;
        _account = account;
        _dialogs = dialogs;
        Playlists = new IncrementalCollection<Playlist>((c, ct) => _library.GetPlaylistsAsync(c, ct), HandleLoadMoreError, () => NavigationToken);
    }

    public IncrementalCollection<Playlist> Playlists { get; }

    protected override string LoadErrorTitle => "Couldn't load your playlists";

    protected override async Task LoadContentAsync(CancellationToken cancellationToken)
    {
        var page = await _library.GetPlaylistsAsync(null, cancellationToken);
        Playlists.Reset(page.Items, page.Continuation);
        IsEmpty = Playlists.Count == 0;
    }

    protected override void ClearContent() => Playlists.Reset([], null);

    [RelayCommand]
    private async Task CreatePlaylistAsync()
    {
        var details = await _dialogs.PromptCreateAsync();
        if (details is null)
        {
            return;
        }

        try
        {
            await _account.CreatePlaylistAsync(details.Title, details.Description, details.Privacy);
            Notifications.Show(new AppNotification(NotificationSeverity.Success, "Playlist created", details.Title));
        }
        catch (Exception ex)
        {
            Notifications.ShowError("Couldn't create the playlist", ex);
            return;
        }

        await LoadAsync();
    }
}
