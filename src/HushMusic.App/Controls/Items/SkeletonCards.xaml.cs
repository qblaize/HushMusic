namespace HushMusic.App.Controls.Items;

/// <summary>Placeholder cards (inside a <see cref="Skeleton"/>): a clipped carousel row, or a wrapping grid.</summary>
public sealed partial class SkeletonCards : UserControl
{
    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count), typeof(int), typeof(SkeletonCards), new PropertyMetadata(8, OnShapeChanged));

    public static readonly DependencyProperty IsCircularProperty = DependencyProperty.Register(
        nameof(IsCircular), typeof(bool), typeof(SkeletonCards), new PropertyMetadata(false, OnShapeChanged));

    public static readonly DependencyProperty IsGridProperty = DependencyProperty.Register(
        nameof(IsGrid), typeof(bool), typeof(SkeletonCards), new PropertyMetadata(false, OnLayoutChanged));

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(object), typeof(SkeletonCards), new PropertyMetadata(null));

    public SkeletonCards()
    {
        InitializeComponent();
        Rebuild();
    }

    public int Count
    {
        get => (int)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    /// <summary>Round art and centred lines (artists).</summary>
    public bool IsCircular
    {
        get => (bool)GetValue(IsCircularProperty);
        set => SetValue(IsCircularProperty, value);
    }

    /// <summary>Wrap into rows (card grid pages) instead of one clipped row.</summary>
    public bool IsGrid
    {
        get => (bool)GetValue(IsGridProperty);
        set => SetValue(IsGridProperty, value);
    }

    internal object? Items
    {
        get => GetValue(ItemsProperty);
        private set => SetValue(ItemsProperty, value);
    }

    public static CornerRadius ArtCorner(bool isRound) =>
        isRound ? new CornerRadius(999) : (CornerRadius)Application.Current.Resources["ArtCornerRadius"];

    public static HorizontalAlignment LineAlignment(bool isRound) => isRound ? HorizontalAlignment.Center : HorizontalAlignment.Left;

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SkeletonCards)d).Rebuild();

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var cards = (SkeletonCards)d;
        cards.Repeater.Layout = (Layout)cards.Resources[cards.IsGrid ? "GridLayout" : "RowLayout"];
    }

    private void Rebuild() => Items = SkeletonData.Create(Count, primary: (90, 60), secondary: (60, 50), isRound: IsCircular);
}
