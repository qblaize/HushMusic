using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using HushMusic.App.Controls.Items;
using HushMusic.App.ViewModels.Pages;
using HushMusic.Core.Features.Stats;

namespace HushMusic.App.Views.Pages;

public sealed partial class StatsPage : Page
{
    public StatsPage()
    {
        InitializeComponent();
    }

    public StatsViewModel ViewModel { get; } = App.GetService<StatsViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ToggleGroup.CheckTag(PeriodTabs, ViewModel.Period.ToString());
        _ = ViewModel.OnNavigatedToAsync(e.Parameter);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    // The initial IsChecked fires during InitializeComponent; the first load comes from OnNavigatedTo instead.
    private void OnPeriodChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } toggle)
        {
            return;
        }

        ToggleGroup.OnChecked(toggle);
        if (IsLoaded && Enum.TryParse<StatsPeriod>(tag, out var period))
        {
            ViewModel.SelectPeriod(period);
        }
    }

    private void OnPeriodUnchecked(object sender, RoutedEventArgs e) => ToggleGroup.OnUnchecked((ToggleButton)sender);

    private void OnRankItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StatsRankItem item })
        {
            ViewModel.OpenRankItemCommand.Execute(item);
        }
    }
}
