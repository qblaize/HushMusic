using System.ComponentModel;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Hosting;
using HushMusic.App.ViewModels.NowPlaying;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// One cover of the <see cref="CoverFlow"/>: the artwork (or a station's logo), a soft drop shadow and a dark overlay
/// for depth. The cover's pose (centre, size, tilt) lives in a Composition property set that the CoverFlow animates;
/// one expression turns it into the visual's transform relative to wherever layout put the card. Layout holds the
/// card's resting box, so hit testing and the focus rectangle match the screen once the motion settles.
/// </summary>
public sealed partial class CoverFlowCard : Button
{
    // A hovered side cover brightens a little.
    private const float HoverDimLift = 0.15f;

    // Centre the card on its own middle, scale it to the pose size, tilt it about the vertical axis, apply perspective
    // (camera at Depth in front of it), then move its middle to the pose centre. Composition adds Offset (the layout
    // position) after the matrix, so it is subtracted here: the cover lands on the pose wherever layout put the card.
    private const string TransformExpression =
        "Matrix4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, -this.Target.Size.X*0.5,-this.Target.Size.Y*0.5,0,1)"
        + " * Matrix4x4(p.Size/Max(this.Target.Size.X,1),0,0,0, 0,p.Size/Max(this.Target.Size.X,1),0,0, 0,0,1,0, 0,0,0,1)"
        + " * Matrix4x4(Cos(p.Rot),0,-Sin(p.Rot),0, 0,1,0,0, Sin(p.Rot),0,Cos(p.Rot),0, 0,0,0,1)"
        + " * Matrix4x4(1,0,0,0, 0,1,0,0, 0,0,1,-1/p.Depth, 0,0,0,1)"
        + " * Matrix4x4(1,0,0,0, 0,1,0,0, 0,0,1,0, p.X-this.Target.Offset.X,p.Y-this.Target.Offset.Y,0,1)";

    private static readonly TimeSpan HoverDuration = TimeSpan.FromMilliseconds(160);

    private readonly Visual _dimVisual;
    private InputSystemCursor? _handCursor;
    private float _dim;
    private bool _hovered;
    private int _decodedPixels;

    public CoverFlowCard()
    {
        InitializeComponent();
        Visual = ElementCompositionPreview.GetElementVisual(this);
        var compositor = Visual.Compositor;

        Pose = compositor.CreatePropertySet();
        Pose.InsertScalar("X", 0);
        Pose.InsertScalar("Y", 0);
        Pose.InsertScalar("Size", 0);
        Pose.InsertScalar("Rot", 0);
        Pose.InsertScalar("Depth", 1000);
        var transform = compositor.CreateExpressionAnimation(TransformExpression);
        transform.SetReferenceParameter("p", Pose);
        Visual.StartAnimation(nameof(Visual.TransformMatrix), transform);

        _dimVisual = ElementCompositionPreview.GetElementVisual(DimLayer);
        _dimVisual.Opacity = 0;
        AddShadow(compositor);

        GotFocus += (_, _) => FocusRing.Opacity = FocusState is FocusState.Keyboard or FocusState.Programmatic ? 1 : 0;
        LostFocus += (_, _) => FocusRing.Opacity = 0;
        PointerEntered += (_, _) => SetHovered(true);
        PointerExited += (_, _) => SetHovered(false);
        PointerCanceled += (_, _) => SetHovered(false);
        PointerCaptureLost += (_, _) => SetHovered(false);
    }

    public CoverFlowItem? Item { get; private set; }

    /// <summary>Where the card rests: 0 is the current item, negative before it, positive after it.</summary>
    public int Slot { get; private set; }

    /// <summary>Gliding out of view; parked (or brought back) when that ends.</summary>
    public bool IsExiting { get; private set; }

    internal Visual Visual { get; }

    /// <summary>X, Y (centre, in the stage), Size, Rot (radians about the vertical axis), Depth (camera distance).</summary>
    internal CompositionPropertySet Pose { get; }

    internal int ExitVersion { get; private set; }

    public void Bind(CoverFlowItem item, int decodePixels, bool animateArt)
    {
        if (ReferenceEquals(Item, item))
        {
            return;
        }

        if (Item is not null)
        {
            Item.PropertyChanged -= OnItemPropertyChanged;
        }

        Item = item;
        item.PropertyChanged += OnItemPropertyChanged;

        // Fallback and size first: the image reloads when the URL changes and reads them then.
        _decodedPixels = decodePixels;
        Art.DecodePixelWidth = decodePixels;
        Art.Animate = animateArt;
        Art.FallbackUrl = item.ArtFallbackUrl;
        Art.Url = item.ArtUrl;
        StationArt.StationName = item.StationName;
        StationArt.LogoUrl = item.IsStation ? item.LogoUrl : null;
        StationArt.Visibility = item.IsStation ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Back to the pool: no artwork, out of reach of pointer, keyboard and automation.</summary>
    public void Unbind()
    {
        if (Item is not null)
        {
            Item.PropertyChanged -= OnItemPropertyChanged;
            Item = null;
        }

        IsExiting = false;
        ExitVersion++;
        Art.Animate = false;
        Art.FallbackUrl = null;
        Art.Url = null;
        StationArt.LogoUrl = null;
        StationArt.Visibility = Visibility.Collapsed;
        _hovered = false;
        SetInteractive(false);
    }

    /// <summary>Art changes cross-fade only while the view is on screen.</summary>
    public void SetArtAnimated(bool animate) => Art.Animate = animate;

    /// <summary>Decodes the art at least this many physical pixels wide (a side cover moving to the middle sharpens).</summary>
    public void EnsureDecode(int pixels)
    {
        if (pixels <= _decodedPixels)
        {
            return;
        }

        _decodedPixels = pixels;
        Art.DecodePixelWidth = pixels;
        if (Item?.ArtUrl is not null)
        {
            Art.Refresh();
        }
    }

    /// <summary>The card's role at its slot: the middle closes Now Playing, the sides play their song.</summary>
    public void SetSlot(int slot)
    {
        Slot = slot;
        if (Item is not { } item)
        {
            return;
        }

        var centre = slot == 0;
        AutomationProperties.SetAutomationId(this, centre ? "NowPlayingArtwork" : slot < 0 ? $"CoverFlowPrev{-slot}" : $"CoverFlowNext{slot}");
        AutomationProperties.SetName(this, centre ? "Close Now Playing" : item.PlayLabel);
        ToolTipService.SetToolTip(this, centre ? null : item.ToolTip);
        TabIndex = slot + CoverFlowViewModel.Reach + 1;
        SetInteractive(true);
        IsTabStop = !centre;
        ProtectedCursor = centre ? null : (_handCursor ??= InputSystemCursor.Create(InputSystemCursorShape.Hand));
    }

    public void SetDim(float dim, TimeSpan? duration)
    {
        _dim = dim;
        ApplyDim(duration);
    }

    internal void BeginExit()
    {
        IsExiting = true;
        ExitVersion++;
        _hovered = false;
        SetInteractive(false);
    }

    internal void CancelExit()
    {
        IsExiting = false;
        ExitVersion++;
    }

    private void SetInteractive(bool interactive)
    {
        IsHitTestVisible = interactive;
        IsTabStop = interactive;
        AutomationProperties.SetAccessibilityView(this, interactive ? AccessibilityView.Content : AccessibilityView.Raw);
        if (!interactive)
        {
            AutomationProperties.SetAutomationId(this, string.Empty);
            ToolTipService.SetToolTip(this, null);
        }
    }

    private void SetHovered(bool hovered)
    {
        if (_hovered != hovered)
        {
            _hovered = hovered;
            ApplyDim(HoverDuration);
        }
    }

    private void ApplyDim(TimeSpan? duration)
    {
        var lift = _hovered && Slot != 0 && !IsExiting ? HoverDimLift : 0f;
        CoverFlowMotion.Animate(_dimVisual, nameof(Visual.Opacity), Math.Max(0f, _dim - lift), duration);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CoverFlowItem.LogoUrl) && Item is { IsStation: true } item)
        {
            StationArt.LogoUrl = item.LogoUrl;
        }
    }

    // Composition shadow: follows the card's tilt and scale (a ThemeShadow would not), and lands on the covers behind.
    private void AddShadow(Compositor compositor)
    {
        var host = ElementCompositionPreview.GetElementVisual(ShadowHost);
        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = 48f;
        shadow.Offset = new Vector3(0, 18, 0);
        shadow.Color = Colors.Black;
        shadow.Opacity = 0.5f;

        var sprite = compositor.CreateSpriteVisual();
        sprite.Brush = compositor.CreateColorBrush(Colors.Black);
        sprite.Shadow = shadow;
        var size = compositor.CreateExpressionAnimation("host.Size");
        size.SetReferenceParameter("host", host);
        sprite.StartAnimation(nameof(Visual.Size), size);
        ElementCompositionPreview.SetElementChildVisual(ShadowHost, sprite);
    }
}
