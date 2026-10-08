using Microsoft.UI.Xaml.Navigation;

namespace HushMusic.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();

        // Automated screenshots can't scroll: HUSHMUSIC_TEST_SETTINGS_SCROLL=end keeps the page at the bottom (About).
        if (Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_SETTINGS_SCROLL") == "end")
        {
            Scroller.SizeChanged += (_, _) => ScrollToEnd();
            ((FrameworkElement)Scroller.Content).SizeChanged += (_, _) => ScrollToEnd();
        }
    }

    public SettingsViewModel ViewModel { get; } = App.GetService<SettingsViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.OnNavigatedToAsync(e.Parameter);

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    private void ScrollToEnd() =>
        DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, Scroller.ScrollableHeight, null, disableAnimation: true));
}
