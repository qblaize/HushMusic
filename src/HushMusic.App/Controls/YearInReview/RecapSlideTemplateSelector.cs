using HushMusic.App.ViewModels.YearInReview;

namespace HushMusic.App.Controls.YearInReview;

/// <summary>Picks the template for each kind of recap slide.</summary>
public sealed partial class RecapSlideTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Intro { get; set; }

    public DataTemplate? Feature { get; set; }

    public DataTemplate? Artist { get; set; }

    public DataTemplate? Ranking { get; set; }

    public DataTemplate? Clock { get; set; }

    public DataTemplate? Days { get; set; }

    public DataTemplate? Radio { get; set; }

    public DataTemplate? Outro { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) => item switch
    {
        IntroSlide => Intro,
        FeatureSlide => Feature,
        ArtistSlide => Artist,
        RankingSlide => Ranking,
        ClockSlide => Clock,
        DaysSlide => Days,
        RadioSlide => Radio,
        OutroSlide => Outro,
        _ => null,
    };

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
