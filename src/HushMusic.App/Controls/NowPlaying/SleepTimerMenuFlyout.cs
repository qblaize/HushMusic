using HushMusic.App.ViewModels.NowPlaying;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>Sleep timer menu (Off, 15 / 30 / 45 minutes, 1 hour, end of track), rebuilt from the timer's state on every open.</summary>
public sealed partial class SleepTimerMenuFlyout : MenuFlyout
{
    public SleepTimerMenuFlyout()
    {
        Opening += (_, _) => Build();
    }

    public SleepTimerViewModel? ViewModel { get; set; }

    private void Build()
    {
        Items.Clear();
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var current = viewModel.Current;
        foreach (var choice in SleepTimerViewModel.Choices)
        {
            if (choice is SleepTimerChoice.Minutes15 or SleepTimerChoice.EndOfTrack)
            {
                Items.Add(new MenuFlyoutSeparator());
            }

            var item = new RadioMenuFlyoutItem
            {
                Text = SleepTimerViewModel.ChoiceText(choice),
                GroupName = "SleepTimer",
                IsChecked = choice == current,
                IsEnabled = choice != SleepTimerChoice.EndOfTrack || viewModel.CanStopAtEndOfTrack,
            };
            item.Click += (_, _) => viewModel.Choose(choice);
            Items.Add(item);
        }
    }
}
