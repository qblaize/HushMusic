using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using HushMusic.App.Controls.Items;
using HushMusic.App.ViewModels.Pages;

namespace HushMusic.App.Views.Pages;

public sealed partial class RadioPage : Page
{
    public RadioPage()
    {
        InitializeComponent();

        // One chip per curated genre (the list is data, so the chips are made here).
        var style = (Style)Resources["GenreChipStyle"];
        foreach (var genre in ViewModel.Genres)
        {
            var chip = new ToggleButton
            {
                Content = genre.Name,
                Tag = genre.Id,
                Style = style,
                IsChecked = genre == ViewModel.SelectedGenre,
            };
            AutomationProperties.SetAutomationId(chip, "RadioGenre-" + genre.Id);
            chip.Checked += OnGenreChecked;
            chip.Unchecked += OnGenreUnchecked;
            GenreChips.Children.Add(chip);
        }
    }

    public RadioViewModel ViewModel { get; } = App.GetService<RadioViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.OnNavigatedToAsync(e.Parameter, e.NavigationMode == NavigationMode.Back);

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    private void OnGenreChecked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string id } chip)
        {
            ToggleGroup.OnChecked(chip);
            ViewModel.SelectGenre(id);
        }
    }

    private void OnGenreUnchecked(object sender, RoutedEventArgs e) => ToggleGroup.OnUnchecked((ToggleButton)sender);
}
