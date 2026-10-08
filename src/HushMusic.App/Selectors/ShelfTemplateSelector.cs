using HushMusic.Core.Models;

namespace HushMusic.App.Selectors;

/// <summary>Picks a shelf template by <see cref="Shelf.Layout"/>: a carousel of cards, or a grid of compact song rows ("Quick picks").</summary>
public sealed partial class ShelfTemplateSelector : DataTemplateSelector
{
    public DataTemplate? CardsTemplate { get; set; }

    public DataTemplate? ListTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) => Select(item) ?? base.SelectTemplateCore(item);

    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) =>
        Select(item) ?? base.SelectTemplateCore(item, container);

    private DataTemplate? Select(object item) =>
        item is Shelf { Layout: ShelfLayout.List } && ListTemplate is not null ? ListTemplate : CardsTemplate;
}
