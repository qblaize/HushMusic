using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>An instrumental break in synced lyrics: three dots that light up one after another through the pause.</summary>
internal sealed partial class LyricsDots : StackPanel
{
    private const double DotSize = 12;

    public LyricsDots(Brush fill)
    {
        Orientation = Orientation.Horizontal;
        Spacing = 9;
        Height = 36;
        Margin = new Thickness(2, 0, 0, 0);
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        for (var i = 0; i < 3; i++)
        {
            var dot = new Ellipse
            {
                Width = DotSize,
                Height = DotSize,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = fill,
            };
            Children.Add(dot);
            var visual = ElementCompositionPreview.GetElementVisual(dot);
            visual.CenterPoint = new Vector3((float)DotSize / 2, (float)DotSize / 2, 0);
        }
    }

    public Visual DotVisual(int index) => ElementCompositionPreview.GetElementVisual(Children[index]);
}
