using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Hosting;
using Windows.Foundation;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// A lyric line's words, one TextBlock each, laid out left to right and wrapped between words the way text wraps.
/// Syllables of one word (no space between them) stay on one row. Knows where each word sits, for the overlay that
/// lights the words up (<see cref="Overlay"/>, drawn above the words).
/// </summary>
/// <remarks>The TextBlocks are reused when the panel shows another line.</remarks>
internal sealed partial class LyricsWordPanel : Panel
{
    private readonly List<bool> _spaceAfter = [];
    private readonly List<Rect> _rects = [];
    private Style? _wordStyle;
    private double _spaceWidth = -1;
    private ContainerVisual? _overlay;
    private bool _textHidden;

    public LyricsWordPanel()
    {
        // The line button carries the text for screen readers.
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
    }

    /// <summary>The words were placed somewhere else (a new width, new words).</summary>
    public event EventHandler? Arranged;

    public Style? WordStyle
    {
        get => _wordStyle;
        set
        {
            _wordStyle = value;
            _spaceWidth = -1;
            foreach (var child in Children)
            {
                ((TextBlock)child).Style = value;
            }
        }
    }

    public int Count => Children.Count;

    public TextBlock WordAt(int index) => (TextBlock)Children[index];

    /// <summary>Where word <paramref name="index"/> sits in the panel (empty until the first layout).</summary>
    public Rect WordRect(int index) => index < _rects.Count ? _rects[index] : default;

    /// <summary>Font size of the words (for sizes that scale with the text).</summary>
    public double FontSize => Children.Count > 0 ? WordAt(0).FontSize : 28;

    public void SetWords(IReadOnlyList<LyricWordTiming> words)
    {
        while (Children.Count > words.Count)
        {
            Children.RemoveAt(Children.Count - 1);
        }

        _spaceAfter.Clear();
        for (var i = 0; i < words.Count; i++)
        {
            if (i < Children.Count)
            {
                WordAt(i).Text = words[i].Text;
            }
            else
            {
                var word = new TextBlock { Style = _wordStyle, Text = words[i].Text };
                AutomationProperties.SetAccessibilityView(word, AccessibilityView.Raw);
                if (_textHidden)
                {
                    ElementCompositionPreview.GetElementVisual(word).Opacity = 0;
                }

                Children.Add(word);
            }

            _spaceAfter.Add(words[i].SpaceAfter);
        }

        _rects.Clear();
        InvalidateMeasure();
    }

    /// <summary>A visual drawn above the words, in panel coordinates; created on first use.</summary>
    public ContainerVisual Overlay(Compositor compositor)
    {
        if (_overlay is null)
        {
            _overlay = compositor.CreateContainerVisual();
            ElementCompositionPreview.SetElementChildVisual(this, _overlay);
        }

        return _overlay;
    }

    /// <summary>Hides the TextBlocks (the overlay draws the words meanwhile) or shows them again.</summary>
    public void SetTextHidden(bool hidden)
    {
        if (hidden == _textHidden)
        {
            return;
        }

        _textHidden = hidden;

        // Only the Composition opacity: XAML keeps rendering the text, so its alpha mask stays current.
        foreach (var child in Children)
        {
            ElementCompositionPreview.GetElementVisual(child).Opacity = hidden ? 0 : 1;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;
        var measure = new Size(width, double.PositiveInfinity);
        foreach (var child in Children)
        {
            child.Measure(measure);
        }

        var size = Layout(width, arrange: false);
        return new Size(double.IsInfinity(width) ? size.Width : Math.Min(size.Width, width), size.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width, arrange: true);
        return finalSize;
    }

    // Rows of words: a word that doesn't fit starts a new row, together with the syllables joined to it. A single word
    // wider than the row wraps inside its own TextBlock.
    private Size Layout(double width, bool arrange)
    {
        var space = SpaceWidth();
        var changed = false;
        if (arrange && _rects.Count != Children.Count)
        {
            _rects.Clear();
            _rects.AddRange(Enumerable.Repeat(default(Rect), Children.Count));
            changed = true;
        }

        double x = 0, y = 0, rowHeight = 0, maxWidth = 0;
        var i = 0;
        while (i < Children.Count)
        {
            // The run of syllables that make one word.
            var end = i;
            var runWidth = Children[i].DesiredSize.Width;
            while (end < Children.Count - 1 && !_spaceAfter[end])
            {
                end++;
                runWidth += Children[end].DesiredSize.Width;
            }

            if (x > 0 && x + runWidth > width)
            {
                y += rowHeight;
                x = 0;
                rowHeight = 0;
            }

            for (var k = i; k <= end; k++)
            {
                var desired = Children[k].DesiredSize;
                if (x > 0 && x + desired.Width > width && runWidth > width)
                {
                    y += rowHeight;
                    x = 0;
                    rowHeight = 0;
                }

                if (arrange)
                {
                    var rect = new Rect(x, y, desired.Width, desired.Height);
                    Children[k].Arrange(rect);
                    if (_rects[k] != rect)
                    {
                        _rects[k] = rect;
                        changed = true;
                    }
                }

                x += desired.Width;
                rowHeight = Math.Max(rowHeight, desired.Height);
                maxWidth = Math.Max(maxWidth, x);
            }

            if (_spaceAfter[end])
            {
                x += space;
            }

            i = end + 1;
        }

        if (arrange && changed)
        {
            Arranged?.Invoke(this, EventArgs.Empty);
        }

        return new Size(maxWidth, y + rowHeight);
    }

    // The width of a space in the word style: "a a" minus "aa".
    private double SpaceWidth()
    {
        if (_spaceWidth >= 0)
        {
            return _spaceWidth;
        }

        var spaced = new TextBlock { Style = _wordStyle, Text = "a a" };
        var joined = new TextBlock { Style = _wordStyle, Text = "aa" };
        spaced.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        joined.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = spaced.DesiredSize.Width - joined.DesiredSize.Width;
        _spaceWidth = width > 0 ? width : spaced.FontSize * 0.27;
        return _spaceWidth;
    }
}
