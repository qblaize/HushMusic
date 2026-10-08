using CommunityToolkit.Mvvm.ComponentModel;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Models;

namespace HushMusic.App.Dialogs.Playlists;

/// <summary>Title / description / privacy form for creating or editing a playlist.</summary>
public sealed partial class PlaylistEditorViewModel : ObservableObject
{
    public PlaylistEditorViewModel(PlaylistDetails? current)
    {
        IsNew = current is null;
        Title = current?.Title ?? string.Empty;
        Description = current?.Description ?? string.Empty;
        PrivacyIndex = (int)(current?.Privacy ?? PrivacyStatus.Private);
    }

    public bool IsNew { get; }

    public string DialogTitle => IsNew ? "New playlist" : "Edit playlist";

    public string PrimaryText => IsNew ? "Create" : "Save";

    /// <summary>Same order as <see cref="PrivacyStatus"/>.</summary>
    public IReadOnlyList<string> PrivacyOptions { get; } = ["Public", "Unlisted", "Private"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Description { get; set; } = string.Empty;

    [ObservableProperty]
    public partial int PrivacyIndex { get; set; }

    public bool IsValid => !string.IsNullOrWhiteSpace(Title);

    /// <summary>True when the form was submitted with Enter (which closes the dialog without a button result).</summary>
    public bool IsSubmitted { get; private set; }

    public void Submit() => IsSubmitted = true;

    public PlaylistDetails ToDetails() => new(
        Title.Trim(),
        string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
        (PrivacyStatus)Math.Clamp(PrivacyIndex, 0, PrivacyOptions.Count - 1));
}
