using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.UI;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Services.Shell;

/// <summary>
/// The app accent: the current track's album-art colour (<see cref="AppSettings.AccentStyle"/> "Artwork") or a fixed
/// <see cref="AccentPresets"/> entry, each tuned for legibility on the theme shown. The shared <c>AccentBrush</c>,
/// <c>AccentSoftBrush</c> and <c>AccentForegroundBrush</c> resources are recoloured in place, so everything that
/// references them follows. <c>AccentOnDarkBrush</c> is always the dark-theme tuning, for surfaces that stay dark in the
/// light theme (Now Playing, mini player). Custom features can read <see cref="Current"/> or listen to <see cref="Changed"/>.
/// </summary>
public interface IAccentColorService
{
    /// <summary>The accent colour being shown (or transitioned to). UI thread.</summary>
    Color Current { get; }

    /// <summary>Raised on the UI thread when a new accent is chosen, as its transition starts.</summary>
    event EventHandler<AccentColorChangedEventArgs>? Changed;

    /// <summary>Starts following the player, the accent setting and the theme. Call once from the UI thread after the window exists.</summary>
    void Start();
}

public sealed class AccentColorChangedEventArgs(Color previous, Color current, TimeSpan transition) : EventArgs
{
    public Color Previous { get; } = previous;

    public Color Current { get; } = current;

    /// <summary>How long the shell takes to blend from <see cref="Previous"/> to <see cref="Current"/>. Zero: switch instantly.</summary>
    public TimeSpan Transition { get; } = transition;
}

public sealed class AccentColorService : IAccentColorService, IDisposable
{
    public static readonly TimeSpan TransitionDuration = TimeSpan.FromMilliseconds(400);

    private const uint SampleSize = 40;
    private const int ArtWidth = 120;
    private static readonly Color FallbackAccent = ColorHelper.FromArgb(0xFF, 0x9D, 0x7B, 0xFA);

    private readonly IPlayer _player;
    private readonly IImageCache _images;
    private readonly IUiDispatcher _dispatcher;
    private readonly ISettingsService _settings;
    private readonly IThemeService _theme;
    private readonly ILogger<AccentColorService> _logger;
    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = new();

    private SolidColorBrush? _accentBrush;
    private SolidColorBrush? _softBrush;
    private SolidColorBrush? _foregroundBrush;
    private SolidColorBrush? _onDarkBrush;
    private Color _defaultAccent = FallbackAccent;
    private Color? _artColor;
    private Color _fromAccent;
    private Color _fromForeground;
    private Color _toForeground;
    private Color _fromOnDark;
    private Color _toOnDark;
    private CancellationTokenSource? _cts;
    private string? _requestedArt;
    private bool _started;
    private bool _animating;

    public AccentColorService(
        IPlayer player,
        IImageCache images,
        IUiDispatcher dispatcher,
        ISettingsService settings,
        IThemeService theme,
        ILogger<AccentColorService> logger)
    {
        _player = player;
        _images = images;
        _dispatcher = dispatcher;
        _settings = settings;
        _theme = theme;
        _logger = logger;
        Current = FallbackAccent;
    }

    public event EventHandler<AccentColorChangedEventArgs>? Changed;

    public Color Current { get; private set; }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _accentBrush = FindResource("AccentBrush") as SolidColorBrush;
        _softBrush = FindResource("AccentSoftBrush") as SolidColorBrush;
        _foregroundBrush = FindResource("AccentForegroundBrush") as SolidColorBrush;
        _onDarkBrush = FindResource("AccentOnDarkBrush") as SolidColorBrush;
        if (FindResource("DefaultAccentColor") is Color defaultColor)
        {
            _defaultAccent = defaultColor;
        }

        // Start on the right colour for the setting and theme, without a visible fade.
        Current = _accentBrush?.Color ?? _defaultAccent;
        Retarget(animate: false);

        _theme.ThemeChanged += OnThemeChanged;
        _settings.Changed += OnSettingsChanged;
        _player.TrackChanged += OnTrackChanged;
        Follow(_player.CurrentTrack);
    }

    public void Dispose()
    {
        _player.TrackChanged -= OnTrackChanged;
        _settings.Changed -= OnSettingsChanged;
        _theme.ThemeChanged -= OnThemeChanged;
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
        }

        if (_animating && _dispatcher.HasThreadAccess)
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }
    }

    // Player and settings events arrive on background threads; ThemeChanged on the UI thread.
    private void OnTrackChanged(object? sender, TrackChangedEventArgs e) => Follow(e.Track);

    private void OnSettingsChanged(object? sender, EventArgs e) => _dispatcher.Run(() => Retarget(animate: true));

    private void OnThemeChanged(object? sender, EventArgs e) => Retarget(animate: true);

    private void Retarget(bool animate) => TransitionTo(Target(_theme.ActualTheme == ElementTheme.Light), Target(light: false), animate);

    /// <summary>What the accent should be now: the preset, else the album-art colour, else the default, tuned for the theme.</summary>
    private Color Target(bool light)
    {
        if (AccentPresets.Find(_settings.Current.AccentStyle) is { } preset)
        {
            return light ? preset.Light : preset.Dark;
        }

        if (_artColor is { } art)
        {
            return AccentPalette.MakeLegible(art, light);
        }

        // The dark default is the designed brand colour; on light it is darkened to stay readable.
        return light ? AccentPalette.MakeLegible(_defaultAccent, onLight: true) : _defaultAccent;
    }

    private void Follow(Track? track)
    {
        // Nothing playing keeps the last accent: no flash back to the default between queue changes.
        if (track is null)
        {
            return;
        }

        var art = track.ThumbnailFor(ArtWidth)?.Url;
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (string.Equals(art, _requestedArt, StringComparison.Ordinal) && _cts is not null)
            {
                return;
            }

            _requestedArt = art;
            _cts?.Cancel();
            cts = new CancellationTokenSource();
            _cts = cts;
        }

        // Extracted even while a preset is active, so switching back to "Artwork" is instant.
        _ = Task.Run(() => ResolveAsync(art, cts.Token));
    }

    private async Task ResolveAsync(string? art, CancellationToken cancellationToken)
    {
        Color? vibrant;
        try
        {
            vibrant = art is null ? null : await ExtractAsync(art, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read an accent colour from {Url}", art);
            vibrant = null;
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            _dispatcher.Run(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _artColor = vibrant;
                    Retarget(animate: true);
                }
            });
        }
    }

    // The raw dominant colour; legibility is applied per theme in Target().
    private async Task<Color?> ExtractAsync(string url, CancellationToken cancellationToken)
    {
        var path = await _images.GetLocalPathAsync(url, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return null;
        }

        byte[] pixels;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        using (var stream = file.AsRandomAccessStream())
        {
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
            var transform = new BitmapTransform
            {
                ScaledWidth = SampleSize,
                ScaledHeight = SampleSize,
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);
            pixels = data.DetachPixelData();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return AccentPalette.PickVibrant(pixels);
    }

    private void TransitionTo(Color accent, Color onDark, bool animate)
    {
        var shown = _accentBrush?.Color ?? Current;
        if (!_started || (accent.Equals(Current) && (_animating || shown.Equals(accent))))
        {
            return;
        }

        var previous = Current;
        Current = accent;
        _toForeground = AccentPalette.ForegroundFor(accent);
        _toOnDark = onDark;
        if (!animate)
        {
            if (_animating)
            {
                CompositionTarget.Rendering -= OnRendering;
                _animating = false;
                _clock.Stop();
            }

            Apply(accent, _toForeground, onDark);
        }
        else
        {
            _fromAccent = _accentBrush?.Color ?? previous;
            _fromForeground = _foregroundBrush?.Color ?? Colors.White;
            _fromOnDark = _onDarkBrush?.Color ?? onDark;
            _clock.Restart();
            if (!_animating)
            {
                _animating = true;
                CompositionTarget.Rendering += OnRendering;
            }
        }

        if (!accent.Equals(previous))
        {
            _logger.LogDebug("Accent colour #{R:X2}{G:X2}{B:X2}", accent.R, accent.G, accent.B);
            Changed?.Invoke(this, new AccentColorChangedEventArgs(previous, accent, animate ? TransitionDuration : TimeSpan.Zero));
        }
    }

    private void OnRendering(object? sender, object e)
    {
        var t = Math.Clamp(_clock.Elapsed / TransitionDuration, 0, 1);

        // Ease in-out (smoothstep): no visible jump at either end.
        var k = t * t * (3 - (2 * t));
        Apply(AccentPalette.Lerp(_fromAccent, Current, k), AccentPalette.Lerp(_fromForeground, _toForeground, k), AccentPalette.Lerp(_fromOnDark, _toOnDark, k));
        if (t >= 1)
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
            _clock.Stop();
        }
    }

    // The indexer (unlike TryGetValue) also searches the merged dictionaries where Theme.xaml lives.
    private object? FindResource(string key)
    {
        try
        {
            return Application.Current.Resources[key];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Resource {Key} not found; the accent will not follow album art", key);
            return null;
        }
    }

    private void Apply(Color accent, Color foreground, Color onDark)
    {
        if (_onDarkBrush is not null)
        {
            _onDarkBrush.Color = onDark;
        }

        if (_accentBrush is not null)
        {
            _accentBrush.Color = accent;
        }

        if (_softBrush is not null)
        {
            _softBrush.Color = AccentPalette.WithAlpha(accent, 0x33);
        }

        if (_foregroundBrush is not null)
        {
            _foregroundBrush.Color = foreground;
        }
    }
}
