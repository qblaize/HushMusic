using Microsoft.UI.Xaml.Controls.Primitives;
using HushMusic.App.ViewModels.RadioMatch;
using HushMusic.App.ViewModels.Shell;
using HushMusic.Core.Models;

namespace HushMusic.App.Controls.RadioMatch;

/// <summary>
/// Opens the "On YouTube Music" flyout for a song heard on a live station: from the player bars' title, Now Playing's
/// title and the "Heard on air" list. Like any flyout it takes the theme of the element it opens from.
/// </summary>
public static class RadioMatchFlyout
{
    /// <summary>Opens the flyout for the song the playing station announced. Returns false when no song is known.</summary>
    public static bool TryShowOnAir(FrameworkElement target, PlayerViewModel player, FlyoutPlacementMode placement)
    {
        ArgumentNullException.ThrowIfNull(player);
        if (player.LiveNowPlaying is not { } song)
        {
            return false;
        }

        ShowAt(target, song, player.StationName, placement);
        return true;
    }

    public static void ShowAt(FrameworkElement target, RadioNowPlaying song, string? stationName, FlyoutPlacementMode placement)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(song);

        var viewModel = App.GetService<RadioMatchViewModel>();
        viewModel.Load(song, stationName);
        var panel = new RadioMatchPanel(viewModel);
        var flyout = new Flyout
        {
            Content = panel,
            FlyoutPresenterStyle = (Style)panel.Resources["RadioMatchFlyoutPresenterStyle"],
        };

        viewModel.CloseRequested += (_, _) => flyout.Hide();
        flyout.Closed += (_, _) => viewModel.Close();
        flyout.ShowAt(target, new FlyoutShowOptions { Placement = placement });
    }
}
