using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Windows.UI;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// The look of synced lyrics over time: every line but the current one dimmed and a little smaller; the current line
/// bright, its words lighting up one by one as they are sung (or its break's dots one after another).
/// </summary>
/// <remarks>
/// <para>
/// Nothing ticks on the UI thread while a line is sung. When a line becomes current, each word gets Composition
/// animations for its own span (a delay until the word starts, then its duration), started from the playback position;
/// a seek, pause or resume (<see cref="Resync"/>) stops them and starts them again from the new position.
/// </para>
/// <para>
/// A lit word is a sprite over its TextBlock, painted through the text's alpha mask with a gradient: bright, a soft edge,
/// then dim. The wipe slides that gradient across the word (Better Lyrics' swipe). While a line is lit its TextBlocks
/// are hidden and the sprites draw the whole line, so a word can float up without leaving its dim copy behind. Long
/// held words also glow (a white drop shadow of the word). Sprites are pooled and reused from line to line.
/// </para>
/// <para>
/// Running animations make the compositor draw every frame, so they only run while <see cref="IsRunning"/> (the tab is
/// on screen and the music plays); otherwise everything holds still at the current position. With Windows animation
/// effects off (<see cref="MotionEnabled"/> false) the current line simply lights up as a whole.
/// </para>
/// </remarks>
internal sealed class LyricsStage
{
    // Better Lyrics: other lines at 30–35 % and scaled down to 95 %; the current line full size.
    private const float DimOpacity = 0.35f;
    private const float InactiveScale = 0.96f;

    // Words float up by this fraction of the font size while sung; held words of a second or more glow.
    private const float LiftRatio = 0.07f;
    private const float GlowOpacity = 0.55f;
    private const float GlowRadius = 12f;

    // The swipe's soft edge: a tenth of the word plus a little, so short words still get a soft edge.
    private const float FeatherRatio = 0.1f;
    private const float MinFeather = 4f;

    // Wipes and glows move in steps of 1/15 s instead of every display frame: each frame of motion redraws the whole
    // frosted Now Playing view, and a wipe at the display's frame rate measured several percent of a core more. The soft
    // edge is at least one step wide, so the steps blend into a continuous fill.
    private const int StepsPerSecond = 15;
    private const float MaxFeatherRatio = 0.5f;

    // Words light up a moment before they are heard (as in Better Lyrics): the eye is slower than the ear.
    private static readonly TimeSpan WordLead = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan MinLift = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan GlowThreshold = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan GlowRise = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan GlowFade = TimeSpan.FromMilliseconds(800);

    // A change of line moves things for less than half a second (the scroll takes as long), all in the same frames.
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan ScaleDuration = TimeSpan.FromMilliseconds(420);
    private static readonly TimeSpan DotFill = TimeSpan.FromMilliseconds(700);

    // cubic-bezier(0.2, 0, 0, 1): a soft, spring-like settle (Better Lyrics' karaoke easing).
    private static readonly CubicBezier Settle = new(0.2, 0, 0, 1);

    private readonly Panel _host;
    private readonly Func<TimeSpan> _clock;
    private readonly Func<LyricLineButton> _createLine;
    private readonly List<LyricLineButton> _lines = [];
    private readonly Stack<LyricLineButton> _spareLines = new();
    private readonly Stack<WordSprite> _spareWords = new();
    private readonly List<KaraokeLine> _fading = [];
    private readonly Dictionary<int, CompositionEasingFunction> _steps = [];
    private readonly Compositor _compositor;
    private readonly CompositionEasingFunction _linear;
    private readonly CompositionEasingFunction _ease;
    private readonly CompositionEasingFunction _settle;
    private readonly Color _bright;
    private readonly Color _dim;
    private KaraokeLine? _karaoke;
    private int _current = -1;
    private bool _motion = true;

    public LyricsStage(Panel host, Func<TimeSpan> clock, Func<LyricLineButton> createLine, Color textColor)
    {
        _host = host;
        _clock = clock;
        _createLine = createLine;
        _compositor = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(host).Compositor;
        _linear = _compositor.CreateLinearEasingFunction();
        _ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1f));
        _settle = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
        _bright = textColor;
        _dim = Color.FromArgb((byte)Math.Round(textColor.A * DimOpacity), textColor.R, textColor.G, textColor.B);
    }

    /// <summary>The line elements, one per item of the lyrics.</summary>
    public IReadOnlyList<LyricLineButton> Lines => _lines;

    /// <summary>Windows animation effects are on: words light up one by one, lines fade and scale.</summary>
    public bool MotionEnabled
    {
        get => _motion;
        set
        {
            if (_motion == value)
            {
                return;
            }

            _motion = value;
            var current = _current;
            EndCurrent(animate: false);
            foreach (var line in _lines)
            {
                SetLine(line, lit: false, animate: false, LineOpacity.Set);
            }

            _current = current;
            BeginCurrent(animate: false);
        }
    }

    /// <summary>The animations may run (the tab is on screen and the music plays).</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Shows new lyrics, reusing the line elements of the old ones.</summary>
    public void SetLines(IReadOnlyList<LyricLineViewModel> items)
    {
        EndCurrent(animate: false);
        foreach (var fading in _fading.ToList())
        {
            Release(fading);
        }

        _current = -1;
        while (_lines.Count > items.Count)
        {
            var line = _lines[^1];
            _lines.RemoveAt(_lines.Count - 1);
            _host.Children.Remove(line);
            _spareLines.Push(line);
        }

        for (var i = 0; i < items.Count; i++)
        {
            if (i == _lines.Count)
            {
                var line = _spareLines.Count > 0 ? _spareLines.Pop() : NewLine();
                _lines.Add(line);
                _host.Children.Add(line);
            }

            _lines[i].Show(items[i]);
            SetLine(_lines[i], lit: false, animate: false, LineOpacity.Set);
        }
    }

    /// <summary>The line (or break) being sung changed.</summary>
    public void SetCurrent(int index)
    {
        if (index == _current)
        {
            return;
        }

        var animate = IsRunning && _motion;
        EndCurrent(animate);
        _current = index;
        BeginCurrent(animate);
    }

    /// <summary>Starts (true) or freezes (false) the animations, e.g. when the music pauses or the tab is hidden.</summary>
    public void SetRunning(bool running)
    {
        if (running == IsRunning)
        {
            return;
        }

        IsRunning = running;
        Resync();
    }

    /// <summary>The clock jumped or the layout moved: restarts the current line's animations from the position now.</summary>
    public void Resync()
    {
        if (_karaoke is { } karaoke)
        {
            Sync(karaoke);
        }
        else if (Current()?.Dots is not null)
        {
            SyncDots(Current()!, animate: IsRunning && _motion);
        }
    }

    private LyricLineButton? Current() => _current >= 0 && _current < _lines.Count ? _lines[_current] : null;

    private LyricLineButton NewLine()
    {
        var line = _createLine();
        line.Main.Arranged += OnWordsArranged;
        line.Backing.Arranged += OnWordsArranged;
        return line;
    }

    // A lit line was laid out again (a new width): its sprites follow the words.
    private void OnWordsArranged(object? sender, EventArgs e)
    {
        if (_karaoke is { } karaoke && (ReferenceEquals(sender, karaoke.Line.Main) || ReferenceEquals(sender, karaoke.Line.Backing)))
        {
            Sync(karaoke);
        }
    }

    private void BeginCurrent(bool animate)
    {
        if (Current() is not { Line: { } item } line)
        {
            return;
        }

        if (item.IsInstrumental)
        {
            // The dots carry the dimming from here on (same look), then light up one by one.
            SetLine(line, lit: true, animate, LineOpacity.Set);
            SyncDots(line, animate);
        }
        else if (item.IsWordTimed && _motion)
        {
            // Likewise the words: the sprites start out dim.
            SetLine(line, lit: true, animate, LineOpacity.Set);
            StartKaraoke(line, item);
        }
        else
        {
            SetLine(line, lit: true, animate, LineOpacity.Fade);
        }
    }

    private void EndCurrent(bool animate)
    {
        if (Current() is not { Line: { } item } line)
        {
            return;
        }

        if (_karaoke is { } karaoke && ReferenceEquals(karaoke.Line, line))
        {
            // The words fade back to dim themselves; the line only shrinks.
            _karaoke = null;
            StopKaraoke(karaoke, animate);
            SetLine(line, lit: false, animate, animate ? LineOpacity.Keep : LineOpacity.Set);
            return;
        }

        if (item.IsInstrumental && line.Dots is { } dots)
        {
            for (var i = 0; i < 3; i++)
            {
                var dot = dots.DotVisual(i);
                dot.StopAnimation("Opacity");
                dot.StopAnimation("Scale");
                dot.Scale = Vector3.One;
                if (animate)
                {
                    dot.StartAnimation("Opacity", Fade(1f, FadeOut));
                }
                else
                {
                    dot.Opacity = 1;
                }
            }
        }

        SetLine(line, lit: false, animate, LineOpacity.Fade);
    }

    // Scale and opacity of a whole line (the opacity is left alone while lit words fade out by themselves).
    private void SetLine(LyricLineButton line, bool lit, bool animate, LineOpacity opacity)
    {
        var visual = line.RootVisual;
        var scale = lit || !_motion ? 1f : InactiveScale;
        visual.StopAnimation("Scale");
        if (animate)
        {
            var grow = _compositor.CreateVector3KeyFrameAnimation();
            grow.InsertKeyFrame(1, new Vector3(scale, scale, 1), _settle);
            grow.Duration = ScaleDuration;
            visual.StartAnimation("Scale", grow);
        }
        else
        {
            visual.Scale = new Vector3(scale, scale, 1);
        }

        if (opacity == LineOpacity.Keep)
        {
            return;
        }

        visual.StopAnimation("Opacity");
        if (opacity == LineOpacity.Fade && animate)
        {
            visual.StartAnimation("Opacity", Fade(lit ? 1 : DimOpacity, lit ? FadeIn : FadeOut));
        }
        else
        {
            visual.Opacity = lit ? 1 : DimOpacity;
        }
    }

    private ScalarKeyFrameAnimation Fade(float to, TimeSpan duration)
    {
        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, to, _ease);
        fade.Duration = duration;
        return fade;
    }

    // ===== Words =====

    private void StartKaraoke(LyricLineButton line, LyricLineViewModel item)
    {
        // The line comes back before its last fade ended (a seek back): drop those sprites now.
        foreach (var fading in _fading.Where(f => ReferenceEquals(f.Line, line)).ToList())
        {
            Release(fading);
        }

        var karaoke = new KaraokeLine(line);
        AddWords(karaoke, line.Main, item.Words, !item.IsEstimated);
        AddWords(karaoke, line.Backing, item.BackingWords, !item.IsEstimated);
        _karaoke = karaoke;
        Sync(karaoke);
    }

    private void AddWords(KaraokeLine karaoke, LyricsWordPanel panel, IReadOnlyList<LyricWordTiming> words, bool canGlow)
    {
        var count = Math.Min(panel.Count, words.Count);
        if (count == 0)
        {
            return;
        }

        var overlay = panel.Overlay(_compositor);
        for (var i = 0; i < count; i++)
        {
            var sprite = _spareWords.Count > 0 ? _spareWords.Pop() : new WordSprite(_compositor, _bright, _dim);
            var mask = panel.WordAt(i).GetAlphaMask();
            sprite.Mask.Mask = mask;
            var glows = canGlow && words[i].Duration >= GlowThreshold;
            if (glows)
            {
                sprite.ShowGlow(_compositor, mask, GlowRadius);
            }

            overlay.Children.InsertAtTop(sprite.Root);
            karaoke.Words.Add(new KaraokeWord(panel, i, words[i], sprite, glows));
        }

        karaoke.Panels.Add(panel);
        panel.SetTextHidden(true);
    }

    // Places every word as it is at the position now and, while running, animates the rest of its span.
    private void Sync(KaraokeLine karaoke)
    {
        var now = _clock() + WordLead;
        var animate = IsRunning && _motion;
        foreach (var word in karaoke.Words)
        {
            Sync(word, now, animate);
        }
    }

    private void Sync(KaraokeWord word, TimeSpan now, bool animate)
    {
        var sprite = word.Sprite;
        sprite.StopAnimations();
        sprite.Bright.Color = _bright;

        var rect = word.Panel.WordRect(word.Index);
        var width = (float)rect.Width;
        sprite.Text.Size = new Vector2(width, (float)rect.Height);
        if (sprite.Glow is { } glowSprite)
        {
            glowSprite.Size = sprite.Text.Size;
        }

        // The gradient's start is the bright edge: from just left of the word (all dim) to its right end (all lit).
        var timing = word.Timing;
        var stride = 1.1f * width / (float)(Math.Max(timing.Duration.TotalSeconds, 0.05) * StepsPerSecond);
        var feather = Math.Clamp(Math.Max((FeatherRatio * width) + 2, stride), MinFeather, Math.Max(MinFeather, width * MaxFeatherRatio));
        sprite.Fill.EndPoint = new Vector2(feather, 0);
        var from = -feather;
        var to = width;
        var progress = Progress(now, timing.Start, timing.Duration);
        sprite.Fill.Offset = new Vector2(from + ((to - from) * progress), 0);

        var lift = (float)(word.Panel.FontSize * LiftRatio);
        var liftSpan = timing.Duration > MinLift ? timing.Duration : MinLift;
        var lifted = Settle.Ease(Progress(now, timing.Start, liftSpan));
        var rest = new Vector3((float)rect.X, (float)rect.Y, 0);
        var up = rest - new Vector3(0, lift, 0);
        sprite.Root.Offset = Vector3.Lerp(rest, up, lifted);

        var glowEnd = timing.End + GlowFade;
        if (word.Glows && sprite.Shadow is { } shadow)
        {
            shadow.Opacity = GlowAt(now, timing);
        }

        if (!animate)
        {
            return;
        }

        var delay = timing.Start > now ? timing.Start - now : TimeSpan.Zero;
        var startsAt = timing.Start > now ? timing.Start : now;
        if (progress < 1)
        {
            var wipe = _compositor.CreateVector2KeyFrameAnimation();
            wipe.InsertKeyFrame(0, sprite.Fill.Offset, _linear);
            wipe.InsertKeyFrame(1, new Vector2(to, 0), Steps(timing.End - startsAt));
            Time(wipe, timing.End - startsAt, delay);
            sprite.Fill.StartAnimation("Offset", wipe);
        }

        if (lifted < 1)
        {
            // Two hops of a pixel or so rather than a glide: the float adds two frames, not one per step.
            var rise = _compositor.CreateVector3KeyFrameAnimation();
            rise.InsertKeyFrame(0, sprite.Root.Offset, _linear);
            rise.InsertKeyFrame(0.3f, Vector3.Lerp(sprite.Root.Offset, up, 0.6f), Steps(1));
            rise.InsertKeyFrame(1, up, Steps(1));
            Time(rise, timing.Start + liftSpan - startsAt, delay);
            sprite.Root.StartAnimation("Offset", rise);
        }

        if (word.Glows && sprite.Shadow is { } glow && now < glowEnd)
        {
            // Rises as the word starts, holds while it is held, then fades out after it.
            var total = glowEnd - startsAt;
            var glowing = _compositor.CreateScalarKeyFrameAnimation();
            glowing.InsertKeyFrame(0, glow.Opacity, _linear);
            var risen = timing.Start + GlowRise - startsAt;
            if (risen > TimeSpan.Zero)
            {
                glowing.InsertKeyFrame((float)(risen / total), GlowOpacity, Steps(risen));
            }

            var held = timing.End - startsAt;
            if (held > TimeSpan.Zero)
            {
                glowing.InsertKeyFrame((float)(held / total), GlowOpacity, _linear);
            }

            glowing.InsertKeyFrame(1, 0, Steps(GlowFade));
            Time(glowing, total, delay);
            glow.StartAnimation("Opacity", glowing);
        }
    }

    // The line is over: lit words fade back to dim and settle, then the sprites go back to the pool and the TextBlocks
    // take over again (dimmed by the line).
    private void StopKaraoke(KaraokeLine karaoke, bool animate)
    {
        if (!animate)
        {
            Release(karaoke);
            return;
        }

        _fading.Add(karaoke);
        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        foreach (var word in karaoke.Words)
        {
            var sprite = word.Sprite;
            sprite.StopAnimations();
            var dim = _compositor.CreateColorKeyFrameAnimation();
            dim.InsertKeyFrame(1, _dim, _ease);
            dim.Duration = FadeOut;
            sprite.Bright.StartAnimation("Color", dim);

            var rect = word.Panel.WordRect(word.Index);
            var settle = _compositor.CreateVector3KeyFrameAnimation();
            settle.InsertKeyFrame(1, new Vector3((float)rect.X, (float)rect.Y, 0), _settle);
            settle.Duration = FadeOut;
            sprite.Root.StartAnimation("Offset", settle);

            if (sprite.Shadow is { } shadow && word.Glows)
            {
                shadow.StartAnimation("Opacity", Fade(0, FadeOut));
            }
        }

        batch.End();
        batch.Completed += (_, _) => _host.DispatcherQueue.TryEnqueue(() => Release(karaoke));
    }

    private void Release(KaraokeLine karaoke)
    {
        if (karaoke.Released)
        {
            return;
        }

        karaoke.Released = true;
        _fading.Remove(karaoke);
        foreach (var word in karaoke.Words)
        {
            word.Sprite.StopAnimations();
            word.Panel.Overlay(_compositor).Children.Remove(word.Sprite.Root);
            word.Sprite.Reset();
            _spareWords.Push(word.Sprite);
        }

        // Unless the line is lit again already, its own text shows again, dimmed by the line.
        if (_karaoke is null || !ReferenceEquals(_karaoke.Line, karaoke.Line))
        {
            foreach (var panel in karaoke.Panels)
            {
                panel.SetTextHidden(false);
            }

            if (!ReferenceEquals(Current(), karaoke.Line))
            {
                karaoke.Line.RootVisual.StopAnimation("Opacity");
                karaoke.Line.RootVisual.Opacity = DimOpacity;
            }
        }
    }

    // ===== Instrumental breaks =====

    // The break is split in thirds; each dot lights up (with a little pulse) as its third begins.
    private void SyncDots(LyricLineButton line, bool animate)
    {
        if (line is not { Line: { } item, Dots: { } dots })
        {
            return;
        }

        var now = _clock();
        var third = (item.End - item.Start) / 3;
        for (var i = 0; i < 3; i++)
        {
            var dot = dots.DotVisual(i);
            dot.StopAnimation("Opacity");
            dot.StopAnimation("Scale");
            dot.Scale = Vector3.One;
            var at = item.Start + (third * i);
            var lit = !_motion || now >= at;
            dot.Opacity = lit ? 1 : DimOpacity;
            if (!animate || lit)
            {
                continue;
            }

            var delay = at - now;
            var fill = _compositor.CreateScalarKeyFrameAnimation();
            fill.InsertKeyFrame(0, DimOpacity, _linear);
            fill.InsertKeyFrame(0.5f, 1, _settle);
            Time(fill, DotFill, delay);
            dot.StartAnimation("Opacity", fill);

            var pulse = _compositor.CreateVector3KeyFrameAnimation();
            pulse.InsertKeyFrame(0, Vector3.One, _linear);
            pulse.InsertKeyFrame(0.35f, new Vector3(1.3f, 1.3f, 1), _settle);
            pulse.InsertKeyFrame(1, Vector3.One, _ease);
            Time(pulse, DotFill, delay);
            dot.StartAnimation("Scale", pulse);
        }
    }

    // ===== Helpers =====

    // A linear change in steps of 1/StepsPerSecond seconds (each step is one frame of the whole view).
    private CompositionEasingFunction Steps(TimeSpan duration) => Steps(Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds * StepsPerSecond)));

    private CompositionEasingFunction Steps(int count)
    {
        if (!_steps.TryGetValue(count, out var steps))
        {
            steps = _compositor.CreateStepEasingFunction(count);
            _steps[count] = steps;
        }

        return steps;
    }

    private static void Time(KeyFrameAnimation animation, TimeSpan duration, TimeSpan delay)
    {
        animation.Duration = duration > TimeSpan.FromMilliseconds(1) ? duration : TimeSpan.FromMilliseconds(1);
        if (delay > TimeSpan.Zero)
        {
            animation.DelayTime = delay;
            animation.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        }
    }

    private static float Progress(TimeSpan now, TimeSpan start, TimeSpan duration) =>
        duration <= TimeSpan.Zero ? (now >= start ? 1 : 0) : (float)Math.Clamp((now - start) / duration, 0, 1);

    private static float GlowAt(TimeSpan now, LyricWordTiming timing)
    {
        if (now < timing.Start || now >= timing.End + GlowFade)
        {
            return 0;
        }

        if (now < timing.Start + GlowRise)
        {
            return GlowOpacity * Settle.Ease((float)((now - timing.Start) / GlowRise));
        }

        return now < timing.End ? GlowOpacity : GlowOpacity * (1 - (float)((now - timing.End) / GlowFade));
    }

    private enum LineOpacity
    {
        Fade,
        Set,
        Keep,
    }

    private sealed class KaraokeLine(LyricLineButton line)
    {
        public LyricLineButton Line { get; } = line;

        public List<KaraokeWord> Words { get; } = [];

        public List<LyricsWordPanel> Panels { get; } = [];

        public bool Released { get; set; }
    }

    private sealed record KaraokeWord(LyricsWordPanel Panel, int Index, LyricWordTiming Timing, WordSprite Sprite, bool Glows);

    /// <summary>The visuals that draw one lit word: its text through a sliding gradient, and an optional glow under it.</summary>
    private sealed class WordSprite
    {
        public WordSprite(Compositor compositor, Color bright, Color dim)
        {
            Fill = compositor.CreateLinearGradientBrush();
            Fill.MappingMode = CompositionMappingMode.Absolute;
            Fill.StartPoint = Vector2.Zero;
            Fill.EndPoint = new Vector2(MinFeather, 0);
            Fill.ExtendMode = CompositionGradientExtendMode.Clamp;
            Bright = compositor.CreateColorGradientStop(0, bright);
            Fill.ColorStops.Add(Bright);
            Fill.ColorStops.Add(compositor.CreateColorGradientStop(1, dim));
            Mask = compositor.CreateMaskBrush();
            Mask.Source = Fill;
            Text = compositor.CreateSpriteVisual();
            Text.Brush = Mask;
            Root = compositor.CreateContainerVisual();
            Root.Children.InsertAtTop(Text);
            BrightColor = bright;
        }

        public ContainerVisual Root { get; }

        public SpriteVisual Text { get; }

        public CompositionMaskBrush Mask { get; }

        public CompositionLinearGradientBrush Fill { get; }

        public CompositionColorGradientStop Bright { get; }

        public SpriteVisual? Glow { get; private set; }

        public DropShadow? Shadow { get; private set; }

        private Color BrightColor { get; }

        public void ShowGlow(Compositor compositor, CompositionBrush mask, float radius)
        {
            if (Glow is null || Shadow is null)
            {
                Shadow = compositor.CreateDropShadow();
                Shadow.Color = Colors.White;
                Shadow.BlurRadius = radius;
                Shadow.Offset = Vector3.Zero;
                Glow = compositor.CreateSpriteVisual();
                Glow.Shadow = Shadow;
                Root.Children.InsertAtBottom(Glow);
            }

            Shadow.Mask = mask;
            Shadow.Opacity = 0;
            Glow.IsVisible = true;
        }

        public void StopAnimations()
        {
            Fill.StopAnimation("Offset");
            Bright.StopAnimation("Color");
            Root.StopAnimation("Offset");
            Shadow?.StopAnimation("Opacity");
        }

        public void Reset()
        {
            Mask.Mask = null;
            Bright.Color = BrightColor;
            if (Glow is not null && Shadow is not null)
            {
                Shadow.Mask = null;
                Shadow.Opacity = 0;
                Glow.IsVisible = false;
            }
        }
    }
}
