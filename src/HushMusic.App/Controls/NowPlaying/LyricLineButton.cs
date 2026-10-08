using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>A synced lyric line: bright while it is being sung, dimmed otherwise. Clicking it seeks there.</summary>
public sealed partial class LyricLineButton : Button
{
    public static readonly DependencyProperty IsCurrentProperty = DependencyProperty.Register(
        nameof(IsCurrent), typeof(bool), typeof(LyricLineButton), new PropertyMetadata(false, (d, _) => ((LyricLineButton)d).Update(animate: true)));

    private const double DimOpacity = 0.34;
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(260);
    private static readonly UISettings UiSettings = new();

    private Storyboard? _fade;

    public LyricLineButton()
    {
        Opacity = DimOpacity;
        Loaded += (_, _) => Update(animate: false);
    }

    public bool IsCurrent
    {
        get => (bool)GetValue(IsCurrentProperty);
        set => SetValue(IsCurrentProperty, value);
    }

    private void Update(bool animate)
    {
        var target = IsCurrent ? 1 : DimOpacity;
        _fade?.Stop();
        _fade = null;
        if (!animate || !IsLoaded || !UiSettings.AnimationsEnabled)
        {
            Opacity = target;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = target,
            Duration = FadeDuration,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(animation, this);
        Storyboard.SetTargetProperty(animation, nameof(Opacity));
        _fade = new Storyboard();
        _fade.Children.Add(animation);
        _fade.Begin();
    }
}
