using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// One item of the synced lyrics: a line's words (with backing vocals in smaller type under them) or an instrumental
/// break's dots. Clicking it seeks there. <see cref="LyricsStage"/> decides how bright it is; the element is reused
/// for another line when new lyrics come in.
/// </summary>
public sealed partial class LyricLineButton : Button
{
    private readonly Grid _root = new();
    private readonly Brush _dotFill;
    private LyricsDots? _dots;

    internal LyricLineButton(Style buttonStyle, Style wordStyle, Style backingStyle, Brush dotFill)
    {
        Style = buttonStyle;
        _dotFill = dotFill;
        Main = new LyricsWordPanel { WordStyle = wordStyle };
        Backing = new LyricsWordPanel { WordStyle = backingStyle, Margin = new Thickness(0, 2, 0, 0), Visibility = Visibility.Collapsed };
        var stack = new StackPanel();
        stack.Children.Add(Main);
        stack.Children.Add(Backing);
        _root.Children.Add(stack);
        Content = _root;

        // Lines scale from their left edge, so the start of the line stays put.
        RootVisual = ElementCompositionPreview.GetElementVisual(_root);
        _root.SizeChanged += (_, e) => RootVisual.CenterPoint = new Vector3(0, (float)e.NewSize.Height / 2, 0);
    }

    internal LyricLineViewModel? Line { get; private set; }

    internal LyricsWordPanel Main { get; }

    internal LyricsWordPanel Backing { get; }

    /// <summary>The dots of an instrumental break (null for a sung line).</summary>
    internal LyricsDots? Dots => Line?.IsInstrumental == true ? _dots : null;

    /// <summary>The line's content: its opacity dims the line, its scale shrinks it a little while it isn't current.</summary>
    internal Visual RootVisual { get; }

    internal void Show(LyricLineViewModel line)
    {
        Line = line;
        AutomationProperties.SetName(this, line.Text);
        if (line.IsInstrumental)
        {
            _dots ??= new LyricsDots(_dotFill);
            if (_dots.Parent is null)
            {
                _root.Children.Add(_dots);
            }

            _dots.Visibility = Visibility.Visible;
            Main.Visibility = Visibility.Collapsed;
            Backing.Visibility = Visibility.Collapsed;
            Main.SetWords([]);
            Backing.SetWords([]);
            return;
        }

        if (_dots is not null)
        {
            _dots.Visibility = Visibility.Collapsed;
        }

        Main.Visibility = Visibility.Visible;
        Main.SetWords(line.Words);
        Backing.SetWords(line.BackingWords);
        Backing.Visibility = line.BackingWords.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
