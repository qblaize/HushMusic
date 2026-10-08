using System.Collections.ObjectModel;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.ViewModels.Pages;

public sealed partial class HistoryViewModel(ILibraryApi library, PageServices services) : SignedInPageViewModelBase(services)
{
    /// <summary>One group per day ("Today", "Yesterday", ...).</summary>
    public ObservableCollection<MediaGroup> Days { get; } = [];

    protected override string LoadErrorTitle => "Couldn't load your history";

    protected override async Task LoadContentAsync(CancellationToken cancellationToken)
    {
        var shelves = await library.GetHistoryAsync(cancellationToken);
        Days.Clear();
        foreach (var shelf in shelves.Where(s => s.Items.Count > 0))
        {
            Days.Add(new MediaGroup(shelf.Title, shelf.Items));
        }

        IsEmpty = Days.Count == 0;
    }

    protected override void ClearContent() => Days.Clear();
}
