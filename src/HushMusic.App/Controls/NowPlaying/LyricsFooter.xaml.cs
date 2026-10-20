using System.ComponentModel;
using Microsoft.UI.Xaml.Automation;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// The line under the lyrics: "Lyrics from …" (a link to the source's page when it has one) and the "Switch source"
/// menu. Opening the menu asks the sources not tried yet for this song, so it can list all that have lyrics.
/// </summary>
public sealed partial class LyricsFooter : UserControl
{
    private bool _menuOpen;

    public LyricsFooter()
    {
        InitializeComponent();
        Loaded += (_, _) => ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public LyricsViewModel ViewModel { get; } = App.GetService<LyricsViewModel>();

    private void OnSourceMenuOpening(object? sender, object e)
    {
        _menuOpen = true;
        FillMenu();
        if (!ViewModel.HasSearchedSources)
        {
            _ = ViewModel.FindOtherSourcesAsync();
        }
    }

    private void OnSourceMenuClosed(object? sender, object e) => _menuOpen = false;

    // The list grows while the menu is open, as the other sources answer.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_menuOpen && e.PropertyName is nameof(LyricsViewModel.AvailableSources) or nameof(LyricsViewModel.IsFindingSources))
        {
            FillMenu();
        }
    }

    private void FillMenu()
    {
        var items = SourceMenu.Items;
        items.Clear();
        foreach (var source in ViewModel.AvailableSources)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = source.ProviderName,
                KeyboardAcceleratorTextOverride = LyricsViewModel.SyncLabel(source.Sync),
                GroupName = "LyricsSource",
                IsChecked = source.Key == ViewModel.SourceKey,
            };
            AutomationProperties.SetName(item, $"{source.ProviderName}, {LyricsViewModel.SyncLabel(source.Sync)}");

            // Checking an item (a click, or a screen reader's toggle) picks that source.
            var key = source.Key;
            item.RegisterPropertyChangedCallback(RadioMenuFlyoutItem.IsCheckedProperty, (_, _) =>
            {
                if (item.IsChecked && key != ViewModel.SourceKey)
                {
                    SourceMenu.Hide();
                    ViewModel.SwitchSourceCommand.Execute(key);
                }
            });
            items.Add(item);
        }

        if (ViewModel.IsFindingSources || ViewModel.AvailableSources.Count <= 1)
        {
            if (items.Count > 0)
            {
                items.Add(new MenuFlyoutSeparator());
            }

            items.Add(new MenuFlyoutItem
            {
                Text = ViewModel.IsFindingSources ? "Looking for other sources…" : "No other source has lyrics for this song",
                IsEnabled = false,
            });
        }
    }
}
