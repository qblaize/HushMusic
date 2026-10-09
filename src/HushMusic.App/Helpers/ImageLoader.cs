using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using HushMusic.App.Services.Shell;

namespace HushMusic.App.Helpers;

/// <summary>
/// Attached property for album art: <c>helpers:ImageLoader.Url="{x:Bind Item.BestThumbnail.Url}"</c>
/// on an <see cref="Image"/>, <see cref="ImageBrush"/> or <see cref="PersonPicture"/>.
/// <list type="bullet">
/// <item>Images go through the disk cache (<see cref="IImageCache"/>) and fall back to a direct network load.</item>
/// <item>
/// They are decoded at the size they are shown at (times the display's scale), never at the source's full size, which
/// is often several times bigger: an <see cref="Image"/> uses its own or its parent's Width and Height, else its
/// laid-out size (the load waits for layout), and loads again if its slot grows. <c>helpers:ImageLoader.DecodeWidth="56"</c>
/// (logical pixels) sets the width instead; an <see cref="ImageBrush"/> needs it, a <see cref="PersonPicture"/> uses its Width.
/// </item>
/// <item>
/// The decoded bitmap is let go of while it can't be seen: when its element leaves the tree (a cached page that was
/// navigated away from, a closed flyout) and while the main window is minimized or hidden. It loads again, from the
/// disk cache, when it is back.
/// </item>
/// </list>
/// </summary>
public static class ImageLoader
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url",
        typeof(string),
        typeof(ImageLoader),
        new PropertyMetadata(null, OnUrlChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth",
        typeof(int),
        typeof(ImageLoader),
        new PropertyMetadata(0, OnDecodeWidthChanged));

    // Laid-out sizes are rounded up to this many logical pixels, so small layout changes don't decode again.
    private const double LayoutStep = 8;

    // Entries of elements that went away without an Unloaded are dropped once there are this many (or twice as many as last time).
    private const int MinPruneCount = 512;

    // Per-element load state, kept on the element itself.
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State",
        typeof(object),
        typeof(ImageLoader),
        new PropertyMetadata(null));

    // Art on screen in the main window, and art let go of while the window was hidden (reloaded when it is shown).
    private static readonly HashSet<LoadState> s_shown = [];
    private static readonly HashSet<LoadState> s_hiddenWithWindow = [];
    private static int s_pruneAt = MinPruneCount;
    private static IImageCache? _cache;
    private static ILogger? _logger;
    private static bool _servicesResolved;
    private static bool _presenceHooked;

    public static string? GetUrl(DependencyObject element) => (string?)element.GetValue(UrlProperty);

    public static void SetUrl(DependencyObject element, string? value) => element.SetValue(UrlProperty, value);

    public static int GetDecodeWidth(DependencyObject element) => (int)element.GetValue(DecodeWidthProperty);

    public static void SetDecodeWidth(DependencyObject element, int value) => element.SetValue(DecodeWidthProperty, value);

    private static void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => Load(d, keepShown: false);

    private static void OnDecodeWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (GetUrl(d) is not null)
        {
            Load(d, keepShown: false);
        }
    }

    private static void Load(DependencyObject target, bool keepShown) => _ = LoadAsync(target, StateOf(target), keepShown);

    private static async Task LoadAsync(DependencyObject target, LoadState state, bool keepShown)
    {
        var version = ++state.Version;
        state.Released = false;
        state.WaitingForSize = false;
        var url = GetUrl(target);
        try
        {
            // Cleared right away so a recycled list container never shows the previous item's art (kept while the
            // same art only loads again at a bigger size).
            if (!keepShown)
            {
                Apply(target, null);
            }

            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            if (IsHiddenFromView(target))
            {
                // Loads when the window is shown again.
                state.Released = true;
                s_hiddenWithWindow.Add(state);
                return;
            }

            Uri? source;
            Uri? network = null;
            if (!ImageCache.TryNormalize(url, out var uri))
            {
                source = Uri.TryCreate(url, UriKind.Absolute, out var other) ? other : null;
            }
            else
            {
                string? localPath = null;
                if (ResolveCache() is { } cache)
                {
                    try
                    {
                        localPath = await cache.GetLocalPathAsync(uri.AbsoluteUri);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Image cache miss for {Url}, loading from the network", uri);
                    }
                }

                source = localPath is null ? uri : new Uri(localPath);
                network = localPath is null ? null : uri;
            }

            if (source is null || version != state.Version)
            {
                return;
            }

            if (IsHiddenFromView(target))
            {
                state.Released = true;
                s_hiddenWithWindow.Add(state);
                return;
            }

            if (!TryGetDecodeSize(target, state, out var decode))
            {
                // Not laid out yet: loads when it gets a size (SizeChanged), so it is decoded at that size.
                state.WaitingForSize = true;
                return;
            }

            var bitmap = CreateBitmap(decode);
            if (network is not null)
            {
                bitmap.ImageFailed += (_, args) =>
                {
                    // A corrupt cache entry: drop it and load straight from the network instead.
                    _logger?.LogDebug("Cached image failed to decode ({Error}), falling back to {Url}", args.ErrorMessage, network);
                    _cache?.Remove(network.AbsoluteUri);
                    if (version == state.Version)
                    {
                        var fallback = CreateBitmap(decode);
                        Apply(target, fallback);
                        fallback.UriSource = network;
                    }
                };
            }

            // On its element before it gets a source, the order XAML recommends (decoding then belongs to the element).
            Apply(target, bitmap);
            bitmap.UriSource = source;
            Track(state);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not load image {Url}", url);
        }
    }

    private static BitmapImage CreateBitmap(DecodeSize decode)
    {
        var bitmap = new BitmapImage();
        if (decode.Width > 0 || decode.Height > 0)
        {
            // Logical pixels: multiplied by the display's scale factor, so the art stays sharp at 150 % or 200 %.
            bitmap.DecodePixelType = DecodePixelType.Logical;
            if (decode.Width > 0)
            {
                bitmap.DecodePixelWidth = decode.Width;
            }
            else
            {
                bitmap.DecodePixelHeight = decode.Height;
            }
        }

        return bitmap;
    }

    // The size to decode at: explicit, or from the slot the image fills. False while the slot isn't known yet.
    private static bool TryGetDecodeSize(DependencyObject target, LoadState state, out DecodeSize decode)
    {
        state.Slot = default;
        decode = default;
        var explicitWidth = GetDecodeWidth(target);
        if (explicitWidth > 0)
        {
            // A square slot of that width: landscape art (a video's thumbnail) still covers it when cropped.
            decode = target is Image sized ? ForSlot(new Size(explicitWidth, explicitWidth), sized.Stretch) : new DecodeSize(explicitWidth, 0);
            return true;
        }

        switch (target)
        {
            case PersonPicture picture:
                decode = double.IsNaN(picture.Width) ? default : new DecodeSize((int)Math.Ceiling(picture.Width), 0);
                return true;
            case Image image:
                if (SlotOf(image) is not { } slot)
                {
                    // Laid out at no size although it is on screen: its size depends on the art, so decode it whole.
                    return image.IsLoaded && AnimationGate.IsInVisibleTree(image);
                }

                state.Slot = slot;
                decode = ForSlot(slot, image.Stretch);
                return true;
            default:
                return true;
        }
    }

    // The image's own or its parent's explicit size, else its (or its parent's) laid-out size.
    private static Size? SlotOf(Image image)
    {
        if (Explicit(image) is { } own)
        {
            return own;
        }

        var parent = VisualTreeHelper.GetParent(image) as FrameworkElement;
        if (parent is not null && Explicit(parent) is { } container)
        {
            return container;
        }

        if (Laid(image) is { } laid)
        {
            return laid;
        }

        return parent is not null ? Laid(parent) : null;

        static Size? Explicit(FrameworkElement element) =>
            double.IsNaN(element.Width) || double.IsNaN(element.Height) || element.Width <= 0 || element.Height <= 0
                ? null
                : new Size(element.Width, element.Height);

        static Size? Laid(FrameworkElement element) =>
            element.ActualWidth > 0 && element.ActualHeight > 0
                ? new Size(Math.Ceiling(element.ActualWidth / LayoutStep) * LayoutStep, Math.Ceiling(element.ActualHeight / LayoutStep) * LayoutStep)
                : null;
    }

    // Art is square or landscape. Filling a slot (cropped), the side that limits the crop is decoded at the slot's size;
    // fitting it in, the other one. Either way the decoded art covers what is shown.
    private static DecodeSize ForSlot(Size slot, Stretch stretch)
    {
        var width = (int)Math.Ceiling(slot.Width);
        var height = (int)Math.Ceiling(slot.Height);
        var wide = slot.Width > slot.Height;
        return stretch switch
        {
            Stretch.None => default,
            Stretch.Uniform => wide ? new DecodeSize(0, height) : new DecodeSize(width, 0),
            _ => wide ? new DecodeSize(width, 0) : new DecodeSize(0, height),
        };
    }

    private static void Apply(DependencyObject target, ImageSource? source)
    {
        switch (target)
        {
            case Image image:
                image.Source = source;
                break;
            case ImageBrush brush:
                brush.ImageSource = source;
                break;
            case PersonPicture picture:
                picture.ProfilePicture = source;
                break;
        }
    }

    private static bool HasSource(DependencyObject target) => target switch
    {
        Image image => image.Source is not null,
        ImageBrush brush => brush.ImageSource is not null,
        PersonPicture picture => picture.ProfilePicture is not null,
        _ => false,
    };

    // ===== Letting go of art that can't be seen =====

    private static LoadState StateOf(DependencyObject target)
    {
        HookPresence();
        if (target.GetValue(StateProperty) is LoadState state)
        {
            return state;
        }

        state = new LoadState(target);
        target.SetValue(StateProperty, state);
        if (target is FrameworkElement element)
        {
            element.Loaded += OnLoaded;
            element.Unloaded += OnUnloaded;
            if (element is Image)
            {
                element.SizeChanged += OnSizeChanged;
            }
        }

        return state;
    }

    // Inside the main window while it is minimized or hidden. Other windows (the taskbar player's flyout) still load.
    private static bool IsHiddenFromView(DependencyObject target) => target is UIElement element && WindowPresence.Hides(element);

    private static void Release(DependencyObject target, LoadState state)
    {
        s_shown.Remove(state);
        state.Version++;
        state.Released = true;
        state.WaitingForSize = false;
        if (HasSource(target))
        {
            Apply(target, null);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject target && target.GetValue(StateProperty) is LoadState { Released: true } or LoadState { WaitingForSize: true })
        {
            Load(target, keepShown: false);
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Unloaded can arrive after the element was already loaded again (moved in the tree): only a real leave counts.
        if (sender is FrameworkElement { IsLoaded: false } element && element.GetValue(StateProperty) is LoadState state && GetUrl(element) is not null)
        {
            Release(element, state);
        }
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not Image image || image.GetValue(StateProperty) is not LoadState state || e.NewSize.Width <= 0 || e.NewSize.Height <= 0)
        {
            return;
        }

        if (state.WaitingForSize)
        {
            Load(image, keepShown: false);
        }
        else if (state.Slot is { } slot && (e.NewSize.Width > slot.Width + 0.5 || e.NewSize.Height > slot.Height + 0.5) && SlotOf(image) is { } grown
            && (grown.Width > slot.Width || grown.Height > slot.Height))
        {
            // The slot grew past what the art was decoded for: decode it again, sharper.
            Load(image, keepShown: true);
        }
    }

    // Elements that went away without an Unloaded (a closed window) leave their entry behind: dropped now and then.
    private static void Track(LoadState state)
    {
        if (s_shown.Add(state) && s_shown.Count > s_pruneAt)
        {
            s_shown.RemoveWhere(s => !s.Target.TryGetTarget(out _));
            s_pruneAt = Math.Max(MinPruneCount, s_shown.Count * 2);
        }
    }

    private static void HookPresence()
    {
        if (!_presenceHooked)
        {
            _presenceHooked = true;
            WindowPresence.Changed += OnWindowPresenceChanged;
        }
    }

    private static void OnWindowPresenceChanged(object? sender, EventArgs e)
    {
        if (!WindowPresence.IsShown)
        {
            foreach (var state in s_shown.ToList())
            {
                if (!state.Target.TryGetTarget(out var target))
                {
                    s_shown.Remove(state);
                }
                else if (IsHiddenFromView(target))
                {
                    Release(target, state);
                    s_hiddenWithWindow.Add(state);
                }
            }

            // The bitmaps (and whatever else the UI let go of) are freed once their wrappers are collected.
            UiMemory.CollectSoon();
            return;
        }

        foreach (var state in s_hiddenWithWindow)
        {
            if (state.Released && state.Target.TryGetTarget(out var target) && target is FrameworkElement { IsLoaded: true })
            {
                Load(target, keepShown: false);
            }
        }

        s_hiddenWithWindow.Clear();
    }

    private static IImageCache? ResolveCache()
    {
        if (!_servicesResolved && App.Services is { } services)
        {
            _cache = services.GetService<IImageCache>();
            _logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ImageLoader).FullName!);
            _servicesResolved = true;
        }

        return _cache;
    }

    /// <summary>Logical pixels to decode at: one side (the other follows the art's aspect), or neither for the natural size.</summary>
    private readonly record struct DecodeSize(int Width, int Height);

    private sealed class LoadState(DependencyObject target)
    {
        public WeakReference<DependencyObject> Target { get; } = new(target);

        /// <summary>Bumped by every load and release; a load that finds it changed has been superseded.</summary>
        public int Version { get; set; }

        /// <summary>The art was let go of (or never loaded) because it couldn't be seen; loads when it can again.</summary>
        public bool Released { get; set; }

        /// <summary>Waiting for layout to give the image a size.</summary>
        public bool WaitingForSize { get; set; }

        /// <summary>The slot the current art was decoded for; null for an explicit or natural size.</summary>
        public Size? Slot { get; set; }
    }
}
