using System.ComponentModel;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using HushMusic.App.Controls.Items;
using HushMusic.App.ViewModels.Pages;
using HushMusic.Core.Models;

namespace HushMusic.App.Views.Pages;

public sealed partial class SearchPage : Page
{
    public SearchPage()
    {
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public SearchViewModel ViewModel { get; } = App.GetService<SearchViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.OnNavigatedToAsync(e.Parameter);

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    private void OnItemClick(object sender, ItemClickEventArgs e) => ViewModel.OpenResultCommand.Execute(e.ClickedItem);

    private void OnFilterChecked(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string tag } chip && Enum.TryParse<SearchFilter>(tag, out var filter))
        {
            ToggleGroup.OnChecked(chip);
            ViewModel.SelectFilterCommand.Execute(filter);
        }
    }

    private void OnFilterUnchecked(object sender, RoutedEventArgs e) => ToggleGroup.OnUnchecked((ToggleButton)sender);

    // Keeps the chips in sync when the view model changes the filter ("Show all", new query).
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SearchViewModel.Filter))
        {
            return;
        }

        ToggleGroup.CheckTag(Filters, ViewModel.Filter.ToString());
    }
}
