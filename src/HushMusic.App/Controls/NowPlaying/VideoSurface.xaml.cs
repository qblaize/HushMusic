using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using HushMusic.App.Helpers;
using HushMusic.Playback;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// The playing music video's picture, laid over the cover (<see cref="MusicVideoPlayer"/>; the sound is not touched).
/// It fetches and decodes only while <see cref="IsVideoEnabled"/>, shown (<see cref="SetShown"/>), loaded, laid out
/// with a size and in a window that is on screen; otherwise the video player is let go at once and picked up again, in
/// step, when it is seen. The picture fades in once it follows the sound and out when the item has none, so the cover
/// underneath shows.
/// A parent with rounded corners (the cover's frame) clips it like the cover.
/// </summary>
public sealed partial class VideoSurface : UserControl
{
    public static readonly DependencyProperty IsVideoEnabledProperty = DependencyProperty.Register(
        nameof(IsVideoEnabled), typeof(bool), typeof(VideoSurface), new PropertyMetadata(false, (d, _) => ((VideoSurface)d).Update()));

    public static readonly DependencyProperty MaxVideoHeightProperty = DependencyProperty.Register(
        nameof(MaxVideoHeight), typeof(int), typeof(VideoSurface), new PropertyMetadata(VideoFormats.DefaultMaxHeight));

    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch), typeof(Stretch), typeof(VideoSurface), new PropertyMetadata(Stretch.UniformToFill, (d, e) => ((VideoSurface)d).Picture.Stretch = (Stretch)e.NewValue));

    public static readonly DependencyProperty IsShowingVideoProperty = DependencyProperty.Register(
        nameof(IsShowingVideo), typeof(bool), typeof(VideoSurface), new PropertyMetadata(false));

    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(200);

    private readonly MusicVideoPlayer _video = App.GetService<MusicVideoPlayer>();
    private bool _shown;
    private bool _active;
    private Storyboard? _fade;

    public VideoSurface()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            WindowPresence.Changed += OnWindowPresenceChanged;
            Update();
        };
        Unloaded += (_, _) =>
        {
            WindowPresence.Changed -= OnWindowPresenceChanged;
            Update();
        };

        // Laid out (and sized) before it starts: the size picks the quality, and a collapsed surface has none. A new
        // size while active applies from the next item.
        SizeChanged += (_, _) =>
        {
            Update();
            if (_active)
            {
                _video.Activate(RequestedHeight());
            }
        };
    }

    /// <summary>The user's choice (<c>AppSettings.NowPlayingVideo</c>). Off lets go of the video at once.</summary>
    public bool IsVideoEnabled
    {
        get => (bool)GetValue(IsVideoEnabledProperty);
        set => SetValue(IsVideoEnabledProperty, value);
    }

    /// <summary>Quality cap (<see cref="VideoFormats.DefaultMaxHeight"/>, at most 1080); below it, the size on screen decides.</summary>
    public int MaxVideoHeight
    {
        get => (int)GetValue(MaxVideoHeightProperty);
        set => SetValue(MaxVideoHeightProperty, value);
    }

    /// <summary>UniformToFill (default) fills the cover's frame, cropping the sides of a wide picture; Uniform shows it all.</summary>
    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    /// <summary>True while the picture is on screen (and covers what is underneath).</summary>
    public bool IsShowingVideo
    {
        get => (bool)GetValue(IsShowingVideoProperty);
        private set => SetValue(IsShowingVideoProperty, value);
    }

    /// <summary>Width / height of the picture being shown (16:9 is about 1.78), or null.</summary>
    public double? VideoAspectRatio => _active ? _video.AspectRatio : null;

    /// <summary>Raised on the UI thread when <see cref="IsShowingVideo"/> changes.</summary>
    public event EventHandler? ShowingVideoChanged;

    /// <summary>
    /// Whether the view around it is on screen (e.g. Now Playing open). Starts false: nothing is fetched until a view
    /// says it is seen.
    /// </summary>
    public void SetShown(bool shown)
    {
        _shown = shown;
        Update();
    }

    private void OnWindowPresenceChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        var wanted = IsVideoEnabled && _shown && IsLoaded && ActualWidth > 0 && ActualHeight > 0 && !WindowPresence.Hides(this);
        if (wanted == _active)
        {
            return;
        }

        _active = wanted;
        if (wanted)
        {
            // In the tree (transparent) while active, so frames are already being drawn when the picture fades in.
            _video.StateChanged += OnStateChanged;
            Picture.SetMediaPlayer(_video.Activate(RequestedHeight()));
            Picture.Visibility = Visibility.Visible;
            ApplyState();
        }
        else
        {
            _video.StateChanged -= OnStateChanged;
            Show(false, animate: false);
            Picture.Visibility = Visibility.Collapsed;
            Picture.SetMediaPlayer(null);
            _video.Deactivate();
        }
    }

    // The quality step that fills the frame: a 16:9 picture stretched to fill is as tall as the frame (or as its width
    // allows); shown whole, it is as wide as the frame.
    private int RequestedHeight()
    {
        var scale = XamlRoot?.RasterizationScale ?? 1;
        var width = ActualWidth * scale;
        var height = ActualHeight * scale;
        var tall = Stretch == Stretch.Uniform ? Math.Min(width * 9 / 16, height) : Math.Max(width * 9 / 16, height);
        return VideoFormats.HeightFor(tall, MaxVideoHeight);
    }

    private void OnStateChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(ApplyState);

    private void ApplyState()
    {
        if (_active)
        {
            Show(_video.State == MusicVideoState.Showing, animate: true);
        }
    }

    private void Show(bool showing, bool animate)
    {
        if (showing == IsShowingVideo)
        {
            return;
        }

        _fade?.Stop();
        _fade = null;
        IsShowingVideo = showing;
        if (!animate)
        {
            Picture.Opacity = showing ? 1 : 0;
        }
        else
        {
            var fade = new DoubleAnimation
            {
                To = showing ? 1 : 0,
                Duration = showing ? FadeIn : FadeOut,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(fade, Picture);
            Storyboard.SetTargetProperty(fade, nameof(Opacity));
            var storyboard = new Storyboard { Children = { fade } };
            storyboard.Completed += (_, _) =>
            {
                if (ReferenceEquals(_fade, storyboard))
                {
                    _fade = null;
                    Picture.Opacity = showing ? 1 : 0;
                }
            };
            _fade = storyboard;
            storyboard.Begin();
        }

        ShowingVideoChanged?.Invoke(this, EventArgs.Empty);
    }
}
