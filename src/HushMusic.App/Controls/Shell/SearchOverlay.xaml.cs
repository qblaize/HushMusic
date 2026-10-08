using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.ViewManagement;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Controls.Shell;

/// <summary>
/// The search modal. Logic lives in <see cref="SearchBoxViewModel"/>; this is UI glue: open/close animation,
/// focus in and back out, and keyboard routing (Up/Down/Enter/Esc).
/// </summary>
public sealed partial class SearchOverlay : UserControl
{
    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(140);
    private static readonly Vector3 ClosedScale = new(0.98f, 0.98f, 1f);

    private readonly UISettings _uiSettings = new();
    private DependencyObject? _restoreFocus;
    private int _version;

    public SearchOverlay()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public SearchBoxViewModel ViewModel { get; } = App.GetService<SearchBoxViewModel>();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.FocusRequested += OnFocusRequested;

        if (ViewModel.IsOpen)
        {
            Show();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.FocusRequested -= OnFocusRequested;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SearchBoxViewModel.IsOpen):
                if (ViewModel.IsOpen)
                {
                    Show();
                }
                else
                {
                    Hide();
                }

                break;
            case nameof(SearchBoxViewModel.HighlightedIndex):
                var index = ViewModel.HighlightedIndex;
                if (index >= 0 && index < ViewModel.Suggestions.Count)
                {
                    Results.ScrollIntoView(ViewModel.Suggestions[index]);
                }

                break;
        }
    }

    private void OnFocusRequested(object? sender, EventArgs e) => FocusField();

    private void Show()
    {
        var version = ++_version;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (focused is not null && !IsInside(focused))
        {
            _restoreFocus = focused;
        }

        Visibility = Visibility.Visible;
        Animate(opening: true, version);

        // Focus once the panel is in the layout.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (version == _version)
            {
                FocusField();
            }
        });
    }

    private void Hide()
    {
        var version = ++_version;
        Animate(opening: false, version);
        RestoreFocus();
    }

    private void FocusField()
    {
        QueryBox.Focus(FocusState.Programmatic);
        QueryBox.SelectAll();
    }

    private void RestoreFocus()
    {
        var target = _restoreFocus;
        _restoreFocus = null;
        if (target is Control { IsLoaded: true, Visibility: Visibility.Visible, IsEnabled: true } control)
        {
            control.Focus(FocusState.Programmatic);
        }
    }

    private bool IsInside(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this))
            {
                return true;
            }
        }

        return false;
    }

    private void Animate(bool opening, int version)
    {
        var backdrop = ElementCompositionPreview.GetElementVisual(Backdrop);
        var panel = ElementCompositionPreview.GetElementVisual(Panel);
        var compositor = panel.Compositor;

        // Scale from the search field, horizontally centred.
        var width = Panel.ActualWidth > 0 ? Panel.ActualWidth : Math.Min(Panel.MaxWidth, (XamlRoot?.Size.Width ?? 688) - 48);
        panel.CenterPoint = new Vector3((float)Math.Max(0, width / 2), 32f, 0f);

        if (!_uiSettings.AnimationsEnabled)
        {
            backdrop.StopAnimation("Opacity");
            panel.StopAnimation("Opacity");
            panel.StopAnimation("Scale");
            backdrop.Opacity = panel.Opacity = opening ? 1f : 0f;
            panel.Scale = Vector3.One;
            if (!opening)
            {
                Visibility = Visibility.Collapsed;
            }

            return;
        }

        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));
        var duration = opening ? OpenDuration : CloseDuration;

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = duration;
        if (opening)
        {
            fade.InsertKeyFrame(0f, 0f);
        }

        fade.InsertKeyFrame(1f, opening ? 1f : 0f, easing);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = duration;
        if (opening)
        {
            scale.InsertKeyFrame(0f, ClosedScale);
        }

        scale.InsertKeyFrame(1f, opening ? Vector3.One : ClosedScale, easing);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        backdrop.StartAnimation("Opacity", fade);
        panel.StartAnimation("Opacity", fade);
        panel.StartAnimation("Scale", scale);
        batch.End();

        if (!opening)
        {
            batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (version == _version)
                {
                    Visibility = Visibility.Collapsed;
                }
            });
        }
    }

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            ViewModel.Close();
            e.Handled = true;
        }
    }

    private void OnQueryPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                ViewModel.MoveHighlight(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                ViewModel.MoveHighlight(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                ViewModel.Activate();
                e.Handled = true;
                break;
            case VirtualKey.Delete when ViewModel.HighlightedIndex >= 0:
                // With a recent search highlighted (not the caret in the text), Delete removes it.
                e.Handled = ViewModel.RemoveHighlightedRecent();
                break;
        }
    }

    // Only typing counts as input; the OneWay binding writing Query back into the box does not.
    private void OnQueryTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!string.Equals(QueryBox.Text, ViewModel.Query, StringComparison.Ordinal))
        {
            ViewModel.OnUserInput(QueryBox.Text);
        }
    }

    private void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchSuggestionViewModel suggestion)
        {
            ViewModel.Choose(suggestion);
        }
    }

    private void OnBackdropTapped(object sender, TappedRoutedEventArgs e) => ViewModel.Close();

    private void OnRemoveRecentClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RemoveRecent((sender as FrameworkElement)?.Tag as SearchSuggestionViewModel);
        FocusField();
    }

    private void OnClearRecentClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearRecent();
        FocusField();
    }
}
