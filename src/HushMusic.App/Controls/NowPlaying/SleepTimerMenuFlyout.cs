using HushMusic.App.ViewModels.NowPlaying;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>Sleep timer menu (Off, 15 / 30 / 45 minutes, 1 hour, end of track), rebuilt from the timer's state on every open.</summary>
public sealed partial class SleepTimerMenuFlyout : MenuFlyout
{
    public SleepTimerMenuFlyout()
    {
        Opening += (_, _) => Fill(Items, ViewModel);
    }

    public SleepTimerViewModel? ViewModel { get; set; }

    /// <summary>
    /// Replaces <paramref name="items"/> with the timer's choices, the current one checked. Also fills the Sleep timer
    /// submenu of the minimal player bar's menu.
    /// </summary>
    public static void Fill(IList<MenuFlyoutItemBase> items, SleepTimerViewModel? viewModel)
    {
        items.Clear();
        if (viewModel is null)
        {
            return;
        }

        var current = viewModel.Current;
        foreach (var choice in SleepTimerViewModel.Choices)
        {
            if (choice is SleepTimerChoice.Minutes15 or SleepTimerChoice.EndOfTrack)
            {
                items.Add(new MenuFlyoutSeparator());
            }

            var item = new RadioMenuFlyoutItem
            {
                Text = SleepTimerViewModel.ChoiceText(choice),
                GroupName = "SleepTimer",
                IsChecked = choice == current,
                IsEnabled = choice != SleepTimerChoice.EndOfTrack || viewModel.CanStopAtEndOfTrack,
            };
            item.Click += (_, _) => viewModel.Choose(choice);
            items.Add(item);
        }
    }
}
