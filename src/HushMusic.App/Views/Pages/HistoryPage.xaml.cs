using Microsoft.UI.Xaml.Navigation;
using HushMusic.App.ViewModels.Pages;

namespace HushMusic.App.Views.Pages;

public sealed partial class HistoryPage : Page
{
    public HistoryPage()
    {
        InitializeComponent();
    }

    public HistoryViewModel ViewModel { get; } = App.GetService<HistoryViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.OnNavigatedToAsync(e.Parameter);

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    private void OnItemClick(object sender, ItemClickEventArgs e) => ViewModel.OpenItemCommand.Execute(e.ClickedItem);
}
