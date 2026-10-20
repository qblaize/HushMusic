using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using HushMusic.App.Helpers;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// Dissolves a scrolling area into the backdrop at its top and bottom edges, as Apple Music's lyrics do: the content
/// fades out there and goes soft on its way, instead of being cut off by a hard line.
/// </summary>
/// <remarks>
/// <para>
/// XAML has no opacity mask, so the content is drawn a second way: its Composition visual is rendered into a visual
/// surface, and that surface is painted through a vertical gradient on a sprite above it; the content's own visual is
/// made transparent (Composition opacity only, so XAML still lays it out, hit-tests it and scrolls it). Two bands at the
/// edges paint a blurred copy of the same surface, which takes over as the sharp one fades.
/// </para>
/// <para>
/// A visual surface is rendered at its source size in pixels, not at the display's scale, and without the source
/// visual's own transform. So on a scaled display the content's children are scaled up by the display scale inside the
/// surface, and the brushes scale the surface back down: text stays as sharp as it is elsewhere.
/// </para>
/// <para>
/// Nothing here animates: the surface is redrawn only in frames where the content changes (a scroll, a lit word), and
/// the blur covers just the two bands.
/// </para>
/// </remarks>
internal sealed class EdgeFade
{
    // How far into a band the sharp content is visible (0 at the edge, 1 where the band ends): an eased ramp.
    private static readonly (float At, float Alpha)[] SharpRamp = [(0f, 0f), (0.25f, 0.08f), (0.5f, 0.35f), (0.75f, 0.75f), (1f, 1f)];

    // The blurred copy: strongest a third of the way in, gone at both ends of the band.
    private static readonly (float At, float Alpha)[] BlurRamp = [(0f, 0f), (0.35f, 0.7f), (0.7f, 0.35f), (1f, 0f)];

    private const float BlurAmount = 6f;

    private static CompositionEffectFactory? s_blurFactory;
    private static bool s_blurFailed;

    private readonly FrameworkElement _host;
    private readonly UIElement _content;
    private readonly Visual _source;
    private readonly Compositor _compositor;
    private readonly CompositionVisualSurface _surface;
    private readonly SpriteVisual _sharp;
    private readonly CompositionSurfaceBrush _sharpSource;
    private readonly CompositionLinearGradientBrush _sharpMask;
    private readonly Band? _top;
    private readonly Band? _bottom;
    private readonly float _topHeight;
    private readonly float _bottomHeight;
    private float _scale = 1;

    private EdgeFade(FrameworkElement host, UIElement content, double top, double bottom)
    {
        _host = host;
        _content = content;
        _topHeight = (float)top;
        _bottomHeight = (float)bottom;
        _source = ElementCompositionPreview.GetElementVisual(content);
        _compositor = _source.Compositor;

        _surface = _compositor.CreateVisualSurface();
        _surface.SourceVisual = _source;

        _sharpSource = SurfaceBrush();
        _sharpMask = _compositor.CreateLinearGradientBrush();
        _sharpMask.MappingMode = CompositionMappingMode.Absolute;
        var sharpBrush = _compositor.CreateMaskBrush();
        sharpBrush.Source = _sharpSource;
        sharpBrush.Mask = _sharpMask;
        _sharp = _compositor.CreateSpriteVisual();
        _sharp.Brush = sharpBrush;

        var root = _compositor.CreateContainerVisual();
        root.Children.InsertAtTop(_sharp);
        if (BlurFactory(_compositor) is { } factory)
        {
            _top = new Band(this, factory, bottomEdge: false);
            _bottom = new Band(this, factory, bottomEdge: true);
            root.Children.InsertAtTop(_top.Sprite);
            root.Children.InsertAtTop(_bottom.Sprite);
        }

        ElementCompositionPreview.SetElementChildVisual(host, root);

        // Drawn by the sprite from now on.
        _source.Opacity = 0;

        host.SizeChanged += (_, _) => Update();
        host.Loaded += (_, _) =>
        {
            if (host.XamlRoot is { } xamlRoot)
            {
                xamlRoot.Changed -= OnXamlRootChanged;
                xamlRoot.Changed += OnXamlRootChanged;
            }

            Update();
        };
        host.Unloaded += (_, _) =>
        {
            if (host.XamlRoot is { } xamlRoot)
            {
                xamlRoot.Changed -= OnXamlRootChanged;
            }
        };
        Update();
    }

    /// <summary>Fades <paramref name="content"/> out over <paramref name="top"/> and <paramref name="bottom"/> pixels.</summary>
    /// <param name="host">The panel the content fills; the faded copy is drawn as its child visual, above the content.</param>
    /// <param name="content">The scrolling element (at the host's top left, the host's size).</param>
    /// <param name="top">Height of the top band.</param>
    /// <param name="bottom">Height of the bottom band.</param>
    public static void Attach(FrameworkElement host, UIElement content, double top, double bottom) => _ = new EdgeFade(host, content, top, bottom);

    // The window moved to a display with another scale.
    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if ((float)sender.RasterizationScale != _scale)
        {
            Update();
        }
    }

    private void Update()
    {
        var width = (float)_host.ActualWidth;
        var height = (float)_host.ActualHeight;
        var scale = _scale = (float)(_host.XamlRoot?.RasterizationScale ?? 1);
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(_content); i++)
        {
            if (VisualTreeHelper.GetChild(_content, i) is UIElement child)
            {
                ElementCompositionPreview.GetElementVisual(child).Scale = new Vector3(scale, scale, 1);
            }
        }

        _surface.SourceSize = new Vector2(width, height) * scale;
        _sharp.Size = new Vector2(width, height);
        Place(_sharpSource, scale, 0);

        // The two ramps meet in the middle when the area is shorter than both bands together.
        var top = Math.Min(_topHeight, height / 2);
        var bottom = Math.Min(_bottomHeight, height / 2);
        _sharpMask.EndPoint = new Vector2(0, Math.Max(1, height));
        var stops = _sharpMask.ColorStops;
        stops.Clear();
        if (height > 0)
        {
            foreach (var (at, alpha) in SharpRamp)
            {
                stops.Add(_compositor.CreateColorGradientStop(at * top / height, Alpha(alpha)));
            }

            for (var i = SharpRamp.Length - 1; i >= 0; i--)
            {
                stops.Add(_compositor.CreateColorGradientStop(1 - (SharpRamp[i].At * bottom / height), Alpha(SharpRamp[i].Alpha)));
            }
        }

        _top?.Place(width, top, 0, scale);
        _bottom?.Place(width, bottom, height - bottom, scale);
    }

    private CompositionSurfaceBrush SurfaceBrush()
    {
        var brush = _compositor.CreateSurfaceBrush(_surface);
        brush.Stretch = CompositionStretch.None;
        brush.HorizontalAlignmentRatio = 0;
        brush.VerticalAlignmentRatio = 0;
        return brush;
    }

    // Shows the surface at the content's size, from row y of the content on.
    private static void Place(CompositionSurfaceBrush brush, float scale, float y) =>
        brush.TransformMatrix = Matrix3x2.CreateScale(1 / scale) * Matrix3x2.CreateTranslation(0, -y);

    private static Color Alpha(float alpha) => Color.FromArgb((byte)Math.Round(alpha * 255), 255, 255, 255);

    // One blur description serves every band (all of them on the UI thread's compositor); a system that can't make it
    // gets the fade alone.
    private static CompositionEffectFactory? BlurFactory(Compositor compositor)
    {
        if (s_blurFactory is null && !s_blurFailed)
        {
            try
            {
                var factory = compositor.CreateEffectFactory(BlurEffect.Create(new CompositionEffectSourceParameter("content"), BlurAmount));
                if (factory.LoadStatus == CompositionEffectFactoryLoadStatus.Success)
                {
                    s_blurFactory = factory;
                }
                else
                {
                    s_blurFailed = true;
                }
            }
            catch (Exception)
            {
                s_blurFailed = true;
            }
        }

        return s_blurFactory;
    }

    // A blurred copy of the content along one edge, faded in and out by its own gradient.
    private sealed class Band
    {
        private readonly EdgeFade _owner;
        private readonly CompositionSurfaceBrush _source;
        private readonly CompositionLinearGradientBrush _mask;
        private readonly bool _bottomEdge;

        public Band(EdgeFade owner, CompositionEffectFactory factory, bool bottomEdge)
        {
            _owner = owner;
            _bottomEdge = bottomEdge;
            var compositor = owner._compositor;
            _source = owner.SurfaceBrush();
            var blur = factory.CreateBrush();
            blur.SetSourceParameter("content", _source);
            _mask = compositor.CreateLinearGradientBrush();
            _mask.MappingMode = CompositionMappingMode.Absolute;
            var brush = compositor.CreateMaskBrush();
            brush.Source = blur;
            brush.Mask = _mask;
            Sprite = compositor.CreateSpriteVisual();
            Sprite.Brush = brush;
        }

        public SpriteVisual Sprite { get; }

        public void Place(float width, float height, float y, float scale)
        {
            Sprite.Size = new Vector2(width, height);
            Sprite.Offset = new Vector3(0, y, 0);
            Sprite.IsVisible = height >= 1;
            EdgeFade.Place(_source, scale, y);
            _mask.EndPoint = new Vector2(0, Math.Max(1, height));
            var stops = _mask.ColorStops;
            stops.Clear();
            for (var i = 0; i < BlurRamp.Length; i++)
            {
                // At = 0 is the outer edge: the top of the top band, the bottom of the bottom one.
                var (at, alpha) = BlurRamp[_bottomEdge ? BlurRamp.Length - 1 - i : i];
                stops.Add(_owner._compositor.CreateColorGradientStop(_bottomEdge ? 1 - at : at, Alpha(alpha)));
            }
        }
    }
}
