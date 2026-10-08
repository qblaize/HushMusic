using System.ComponentModel;
using System.Windows.Input;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;
using HushMusic.App.ViewModels.NowPlaying;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// The Now Playing Cover Flow. Data comes from <see cref="CoverFlowViewModel"/> (the items around the current one);
/// this is the layout and motion glue. The control is as tall as the current cover and as wide as the player column;
/// the current cover sits in the middle, and the songs before and after it are stacked behind it on each side:
/// smaller, tilted towards the middle, darker the further back. When the current song changes every cover glides
/// to its new place; covers leaving the window slide out behind the outermost one, new ones slide in from there.
/// Only the covers on screen (plus those still gliding out) exist; cards are recycled.
/// </summary>
public sealed partial class CoverFlow : UserControl
{
    // Side covers: scale 1 / (1 + 0.28 * level) (0.78, 0.64, 0.54), tilted 20 degrees, seen from 2.2 cover sizes away.
    private const double SideScaleStep = 0.28;
    private const double SideAngle = 20 * Math.PI / 180;
    private const double CameraDistance = 2.2;

    // How much of each side cover shows past the one in front of it (in cover sizes), and the gap kept at the edges.
    private const double MinPeek = 0.12;
    private const double MaxPeek = 0.2;
    private const double EdgeMargin = 16;

    // A cover that leaves the queue fades where it is, shrinking a little.
    private const double VanishScale = 0.9;

    // Dark overlay by level: the middle is clear, the covers behind get darker.
    private static readonly float[] DimByLevel = [0f, 0.38f, 0.58f, 0.72f, 0.8f];

    private readonly Dictionary<Guid, CoverFlowCard> _cards = [];
    private readonly Stack<CoverFlowCard> _pool = new();
    private readonly UISettings _uiSettings = new();
    private RectangleClip? _clip;
    private Geometry _geometry;
    private bool _isShown;

    public CoverFlow()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        Loaded += (_, _) =>
        {
            ViewModel.ItemsChanged += OnItemsChanged;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            Apply(animate: false);
        };
        Unloaded += (_, _) =>
        {
            ViewModel.ItemsChanged -= OnItemsChanged;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    public CoverFlowViewModel ViewModel { get; } = App.GetService<CoverFlowViewModel>();

    /// <summary>The middle cover closes Now Playing (as the single artwork does).</summary>
    public ICommand? CloseCommand { get; set; }

    /// <summary>
    /// The view is on screen (true) or hidden (false). Hidden, changes wait (nothing to see, nothing to load) and are
    /// applied without motion when it shows again.
    /// </summary>
    public void SetShown(bool shown)
    {
        _isShown = shown;
        foreach (var card in _cards.Values)
        {
            card.SetArtAnimated(shown);
        }

        if (shown)
        {
            Apply(animate: false);
        }
    }

    /// <summary>Screen readers get the covers in queue order, whatever order the recycled cards are in.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new CoverFlowAutomationPeer(this);

    private void OnItemsChanged(object? sender, EventArgs e) => Apply(animate: true);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CoverFlowViewModel.IsEnabled) && !ViewModel.IsEnabled)
        {
            ReleaseAll();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var size = e.NewSize;
        var stage = ElementCompositionPreview.GetElementVisual(Stage);
        _clip ??= stage.Compositor.CreateRectangleClip();

        // Clip the sides only (to the player column): the middle cover's shadow may fall below the control.
        _clip.Left = 0;
        _clip.Right = (float)size.Width;
        _clip.Top = -(float)size.Height;
        _clip.Bottom = 2 * (float)size.Height;
        stage.Clip = _clip;

        var geometry = Geometry.For(size.Width, size.Height);
        if (geometry != _geometry)
        {
            _geometry = geometry;
            Apply(animate: false);
        }
    }

    // ===== Placing the cards =====

    private void Apply(bool animate)
    {
        if (!ViewModel.IsEnabled)
        {
            ReleaseAll();
            return;
        }

        if (!_isShown || !_geometry.IsValid)
        {
            return;
        }

        animate &= _uiSettings.AnimationsEnabled;
        var focused = _cards.Values.FirstOrDefault(c => c.FocusState is FocusState.Keyboard or FocusState.Programmatic);
        var focusedSlot = focused?.Slot ?? 0;
        var levels = _geometry.Levels;
        var duration = animate ? CoverFlowMotion.Glide : (TimeSpan?)null;
        var wanted = new Dictionary<Guid, CoverFlowItem>();
        foreach (var item in ViewModel.Items)
        {
            if (Math.Abs(item.Offset) <= levels)
            {
                wanted.TryAdd(item.Id, item);
            }
        }

        // Leaving: out past the outermost cover on the side the song went, or (no longer queued) fading in place.
        foreach (var (id, card) in _cards.ToList())
        {
            if (wanted.ContainsKey(id) || card.IsExiting)
            {
                continue;
            }

            if (!animate)
            {
                Park(card);
                continue;
            }

            var pose = ViewModel.OffsetOf(id) is { } offset && offset != 0
                ? PoseFor(Math.Sign(offset) * (levels + 1))
                : PoseFor(card.Slot).Vanished();
            Exit(card, pose);
        }

        var scale = XamlRoot?.RasterizationScale ?? 1.0;
        foreach (var item in wanted.Values)
        {
            if (!_cards.TryGetValue(item.Id, out var card))
            {
                card = Rent();
                _cards[item.Id] = card;
                var target = PoseFor(item.Offset);
                card.Bind(item, DecodePixels(target, scale), animateArt: animate);
                if (animate)
                {
                    // In from where the song was: beyond the outermost cover on its side, or (new to the queue) in place.
                    var before = ViewModel.PreviousOffsetOf(item.Id);
                    var from = before is { } b && b != item.Offset
                        ? PoseFor(Math.Abs(b) > levels ? Math.Sign(b) * (levels + 1) : b) with { Opacity = 0 }
                        : target.Vanished();
                    Move(card, from, duration: null);
                }
            }
            else
            {
                if (card.IsExiting)
                {
                    card.CancelExit();
                }

                // A new item for the same queue entry (e.g. the track was updated): same art, nothing reloads.
                card.Bind(item, DecodePixels(PoseFor(item.Offset), scale), animateArt: animate);
            }

            // Bound without motion (opening the view), the art appeared at once; later changes cross-fade.
            card.SetArtAnimated(true);
            Place(card, item.Offset, duration, scale);
        }

        // Enter on a focused side cover plays it and sends it to the middle (which closes the view): keep the keyboard
        // on the side, at the same place, so Enter again moves on again.
        if (focused is not null && focusedSlot != 0 && (focused.Slot == 0 || focused.IsExiting || focused.Item is null))
        {
            var successor = _cards.Values
                .Where(c => !c.IsExiting && c.Slot != 0)
                .OrderBy(c => Math.Abs(c.Slot - focusedSlot))
                .ThenBy(c => Math.Sign(c.Slot) == Math.Sign(focusedSlot) ? 0 : 1)
                .FirstOrDefault();
            successor?.Focus(focused.FocusState is FocusState.Unfocused ? FocusState.Programmatic : focused.FocusState);
        }
    }

    private void Place(CoverFlowCard card, int slot, TimeSpan? duration, double rasterizationScale)
    {
        var pose = PoseFor(slot);

        // Layout keeps the resting box (hit testing, tooltip placement); the transform glides to it.
        card.Width = pose.Size;
        card.Height = pose.Size;
        Canvas.SetLeft(card, pose.X - (pose.Size / 2));
        Canvas.SetTop(card, pose.Y - (pose.Size / 2));

        // The middle on top; the new middle comes forward over the old one at once.
        Canvas.SetZIndex(card, CoverFlowViewModel.Reach + 2 - Math.Abs(slot));
        card.SetSlot(slot);
        card.EnsureDecode(DecodePixels(pose, rasterizationScale));
        Move(card, pose, duration);
    }

    private void Exit(CoverFlowCard card, Pose pose)
    {
        card.BeginExit();
        var version = card.ExitVersion;
        Canvas.SetZIndex(card, 0);

        var batch = card.Visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Move(card, pose, CoverFlowMotion.Glide);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (card.IsExiting && card.ExitVersion == version)
            {
                Park(card);
            }
        });
    }

    private void Move(CoverFlowCard card, Pose pose, TimeSpan? duration)
    {
        card.Pose.InsertScalar("Depth", (float)(_geometry.Art * CameraDistance));
        CoverFlowMotion.Animate(card.Pose, "X", (float)pose.X, duration);
        CoverFlowMotion.Animate(card.Pose, "Y", (float)pose.Y, duration);
        CoverFlowMotion.Animate(card.Pose, "Size", (float)pose.Size, duration);
        CoverFlowMotion.Animate(card.Pose, "Rot", (float)pose.Rotation, duration);
        CoverFlowMotion.Animate(card.Visual, nameof(Visual.Opacity), (float)pose.Opacity, duration);
        card.SetDim(pose.Dim, duration);
    }

    private Pose PoseFor(int slot)
    {
        var g = _geometry;
        var level = Math.Abs(slot);
        var side = Math.Sign(slot);
        var x = g.Width / 2;
        var y = g.Art / 2;
        if (level == 0)
        {
            return new Pose(x, y, g.Art, 0, 0, 1);
        }

        // Each level's outer edge shows one peek past the level in front. The outer edge is the far one (the inner edge
        // is tilted towards the viewer), so perspective pulls it in a little: measure it projected.
        var size = g.Art / (1 + (SideScaleStep * level));
        var half = size / 2;
        var farHalf = half * Math.Cos(SideAngle) / (1 + (half * Math.Sin(SideAngle) / (g.Art * CameraDistance)));
        var edge = (g.Art / 2) + (level * g.Peek);
        return new Pose(
            x + (side * (edge - farHalf)),
            y,
            size,
            side * SideAngle,
            DimByLevel[Math.Min(level, DimByLevel.Length - 1)],
            level <= g.Levels ? 1 : 0);
    }

    // Physical pixels the art is decoded at, in steps of 64 so a window resize doesn't re-decode at every pixel.
    private static int DecodePixels(Pose pose, double rasterizationScale) =>
        (int)Math.Ceiling(pose.Size * rasterizationScale / 64) * 64;

    // ===== Recycling =====

    private CoverFlowCard Rent()
    {
        if (_pool.TryPop(out var card))
        {
            card.Visibility = Visibility.Visible;
            return card;
        }

        card = new CoverFlowCard();
        card.Click += OnCardClick;
        Stage.Children.Add(card);
        return card;
    }

    private void Park(CoverFlowCard card)
    {
        if (card.Item is { } item && _cards.TryGetValue(item.Id, out var mapped) && ReferenceEquals(mapped, card))
        {
            _cards.Remove(item.Id);
        }

        card.Unbind();
        card.Visibility = Visibility.Collapsed;
        _pool.Push(card);
    }

    // Single artwork: nothing of the Cover Flow stays realized.
    private void ReleaseAll()
    {
        foreach (var card in _cards.Values.Concat(_pool))
        {
            card.Unbind();
            card.Click -= OnCardClick;
        }

        _cards.Clear();
        _pool.Clear();
        Stage.Children.Clear();
    }

    private void OnCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CoverFlowCard { Item: { } item, IsExiting: false } card)
        {
            return;
        }

        if (card.Slot == 0)
        {
            CloseCommand?.Execute(null);
        }
        else
        {
            item.PlayCommand.Execute(null);
        }
    }

    private sealed partial class CoverFlowAutomationPeer(CoverFlow owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override IList<AutomationPeer> GetChildrenCore() =>
        [
            .. owner._cards.Values
                .Where(c => c.Item is not null && !c.IsExiting)
                .OrderBy(c => c.Slot)
                .Select(FrameworkElementAutomationPeer.CreatePeerForElement)
                .OfType<AutomationPeer>(),
        ];

        protected override bool IsControlElementCore() => false;

        protected override bool IsContentElementCore() => false;
    }

    /// <summary>Where a cover rests: its centre in the stage, size, tilt (radians), dark overlay and opacity.</summary>
    private readonly record struct Pose(double X, double Y, double Size, double Rotation, float Dim, double Opacity)
    {
        public Pose Vanished() => this with { Size = Size * VanishScale, Opacity = 0 };
    }

    /// <summary>The stage at its current size: cover size, how many levels fit on each side, and the peek per level.</summary>
    private readonly record struct Geometry(double Width, double Art, int Levels, double Peek)
    {
        public bool IsValid => Width > 0 && Art > 0;

        public static Geometry For(double width, double art)
        {
            if (width <= 0 || art <= 0)
            {
                return default;
            }

            var room = ((width - art) / 2) - EdgeMargin;
            var minPeek = Math.Max(28, art * MinPeek);
            var levels = room < minPeek ? 0 : (int)Math.Min(CoverFlowViewModel.Reach, Math.Floor(room / minPeek));
            var peek = levels == 0 ? minPeek : Math.Min(art * MaxPeek, room / levels);
            return new Geometry(width, art, levels, peek);
        }
    }
}
