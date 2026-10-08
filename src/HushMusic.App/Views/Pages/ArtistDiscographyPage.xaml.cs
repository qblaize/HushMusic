using Microsoft.UI.Xaml.Navigation;
using HushMusic.App.Controls.Items;
using HushMusic.App.ViewModels.Pages;

namespace HushMusic.App.Views.Pages;

public sealed partial class ArtistDiscographyPage : Page
{
    public ArtistDiscographyPage()
    {
        InitializeComponent();
    }

    public ArtistDiscographyViewModel ViewModel { get; } = App.GetService<ArtistDiscographyViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.OnNavigatedToAsync(e.Parameter);

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        CoverAnimation.PrepareFrom((ListViewBase)sender, e.ClickedItem);
        ViewModel.OpenItemCommand.Execute(e.ClickedItem);
    }
}
