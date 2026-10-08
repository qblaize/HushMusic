using HushMusic.App.Services.Shell;
using HushMusic.App.ViewModels.Pages;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// The bar a page shows while its track list is in select mode (<see cref="TrackSelection.IsActive"/>). It floats above
/// the standard player bar, or near the bottom edge with the docked minimal player.
/// </summary>
public sealed partial class SelectionBar : UserControl
{
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.Register(
        nameof(Selection), typeof(TrackSelection), typeof(SelectionBar), new PropertyMetadata(null));

    // The standard player floats over the page's bottom edge (its height plus its margin); the minimal one is docked below.
    private const double AboveFloatingPlayer = 112;
    private const double AboveDockedPlayer = 20;

    private ISettingsService? _settings;

    public SelectionBar()
    {
        InitializeComponent();
    }

    public TrackSelection? Selection
    {
        get => (TrackSelection?)GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings ??= App.GetService<ISettingsService>();
        _settings.Changed += OnSettingsChanged;
        UpdatePlacement();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_settings is not null)
        {
            _settings.Changed -= OnSettingsChanged;
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(UpdatePlacement);

    private void UpdatePlacement()
    {
        var docked = PlayerLayouts.IsMinimal(_settings?.Current.PlayerLayout);
        Margin = new Thickness(16, 0, 16, docked ? AboveDockedPlayer : AboveFloatingPlayer);
    }
}
