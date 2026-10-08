using Microsoft.UI.Xaml.Media.Animation;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// <c>items:Reveal.IsShown="{x:Bind ViewModel.HasContent, Mode=OneWay}"</c> in place of a Visibility binding: the element
/// collapses while false and fades in when it turns true (content replacing a <see cref="Skeleton"/>).
/// </summary>
public static class Reveal
{
    public static readonly DependencyProperty IsShownProperty = DependencyProperty.RegisterAttached(
        "IsShown", typeof(bool), typeof(Reveal), new PropertyMetadata(true, OnIsShownChanged));

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(240);

    public static bool GetIsShown(DependencyObject element) => (bool)element.GetValue(IsShownProperty);

    public static void SetIsShown(DependencyObject element, bool value) => element.SetValue(IsShownProperty, value);

    private static void OnIsShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        var shown = (bool)e.NewValue;
        element.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (shown && Skeleton.AnimationsEnabled)
        {
            FadeIn(element);
        }
    }

    private static void FadeIn(UIElement element)
    {
        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(Duration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, nameof(UIElement.Opacity));
        var storyboard = new Storyboard();
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }
}
