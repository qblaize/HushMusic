using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using HushMusic.App.Controls.Items;
using HushMusic.App.ViewModels.Pages;

namespace HushMusic.App.Views.Pages;

public sealed partial class LibraryPage : Page
{
    public LibraryPage()
    {
        InitializeComponent();
    }

    public LibraryViewModel ViewModel { get; } = App.GetService<LibraryViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.OnNavigatedToAsync(e.Parameter);

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        CoverAnimation.PrepareFrom((ListViewBase)sender, e.ClickedItem);
        ViewModel.OpenItemCommand.Execute(e.ClickedItem);
    }

    private void OnSongClick(object sender, ItemClickEventArgs e) => ViewModel.PlaySongCommand.Execute(e.ClickedItem);

    // The initial IsChecked fires during InitializeComponent; the first load comes from OnNavigatedTo instead.
    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string tag } toggle)
        {
            return;
        }

        ToggleGroup.OnChecked(toggle);
        if (IsLoaded && Enum.TryParse<LibraryTab>(tag, out var tab))
        {
            ViewModel.SelectTabCommand.Execute(tab);
        }
    }

    private void OnTabUnchecked(object sender, RoutedEventArgs e) => ToggleGroup.OnUnchecked((ToggleButton)sender);
}
