using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Documents;
using HushMusic.App.ViewModels.NowPlaying;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>One line of credits ("Artist, Artist — Album") where every name with a page is a link.</summary>
public sealed partial class CreditsText : UserControl
{
    public static readonly DependencyProperty CreditsProperty = DependencyProperty.Register(
        nameof(Credits), typeof(object), typeof(CreditsText), new PropertyMetadata(null, (d, _) => ((CreditsText)d).Rebuild()));

    public static readonly DependencyProperty TextStyleProperty = DependencyProperty.Register(
        nameof(TextStyle), typeof(Style), typeof(CreditsText), new PropertyMetadata(null, OnTextStyleChanged));

    private readonly TextBlock _text = new();

    public CreditsText()
    {
        IsTabStop = false;
        Content = _text;

        // Hyperlinks copy the text colour when they're built; theme brushes are per theme, so rebuild on a switch.
        ActualThemeChanged += (_, _) => Rebuild();
    }

    public IReadOnlyList<CreditLink>? Credits
    {
        get => GetValue(CreditsProperty) as IReadOnlyList<CreditLink>;
        set => SetValue(CreditsProperty, value);
    }

    public Style? TextStyle
    {
        get => (Style?)GetValue(TextStyleProperty);
        set => SetValue(TextStyleProperty, value);
    }

    private static void OnTextStyleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var credits = (CreditsText)d;
        credits._text.Style = e.NewValue as Style;
        credits.Rebuild();
    }

    private void Rebuild()
    {
        _text.Inlines.Clear();
        var credits = Credits ?? [];
        foreach (var credit in credits)
        {
            if (!string.IsNullOrEmpty(credit.Separator))
            {
                _text.Inlines.Add(new Run { Text = credit.Separator });
            }

            if (credit.Open is not { } open)
            {
                _text.Inlines.Add(new Run { Text = credit.Text });
                continue;
            }

            // Same colour as the text around it: the line reads as one quiet credit, not a row of blue links.
            var link = new Hyperlink { UnderlineStyle = UnderlineStyle.None, Foreground = _text.Foreground };
            link.Inlines.Add(new Run { Text = credit.Text });
            link.Click += (_, _) => open();
            _text.Inlines.Add(link);
        }

        AutomationProperties.SetName(this, string.Concat(credits.Select(c => c.Separator + c.Text)));
    }
}
