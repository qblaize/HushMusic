namespace HushMusic.App.Controls.Items;

/// <summary>Placeholder track rows (inside a <see cref="Skeleton"/>). <see cref="ShowNumber"/> mimics album rows.</summary>
public sealed partial class SkeletonRows : UserControl
{
    public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
        nameof(Count), typeof(int), typeof(SkeletonRows), new PropertyMetadata(8, OnShapeChanged));

    public static readonly DependencyProperty ShowNumberProperty = DependencyProperty.Register(
        nameof(ShowNumber), typeof(bool), typeof(SkeletonRows), new PropertyMetadata(false, OnShapeChanged));

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(object), typeof(SkeletonRows), new PropertyMetadata(null));

    public SkeletonRows()
    {
        InitializeComponent();
        Rebuild();
    }

    public int Count
    {
        get => (int)GetValue(CountProperty);
        set => SetValue(CountProperty, value);
    }

    public bool ShowNumber
    {
        get => (bool)GetValue(ShowNumberProperty);
        set => SetValue(ShowNumberProperty, value);
    }

    internal object? Items
    {
        get => GetValue(ItemsProperty);
        private set => SetValue(ItemsProperty, value);
    }

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SkeletonRows)d).Rebuild();

    private void Rebuild() =>
        Items = SkeletonData.Create(
            Count,
            primary: (140, 160),
            secondary: (90, 110),
            leadWidth: ShowNumber ? 14 : 40,
            leadHeight: ShowNumber ? 11 : 40);
}
