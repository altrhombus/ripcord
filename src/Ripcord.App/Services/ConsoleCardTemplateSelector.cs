using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ripcord_App.Services;

/// <summary>
/// The "add a console" tile that sits at the end of the console grid. Carries no data — it exists so the
/// tile can be one more item in the same wrap flow as the real cards, rather than a button parked outside
/// the grid where it stops being part of the same thing.
/// </summary>
public sealed class AddConsolePlaceholder
{
    public static readonly AddConsolePlaceholder Instance = new();

    private AddConsolePlaceholder()
    {
    }
}

/// <summary>
/// Picks the console card or the add tile. The grid holds two kinds of item, and a selector is the only way
/// a single items control can template both.
/// </summary>
public sealed partial class ConsoleCardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ConsoleTemplate { get; set; }

    public DataTemplate? AddTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is AddConsolePlaceholder ? AddTemplate : ConsoleTemplate;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container) =>
        SelectTemplateCore(item);
}
