using Microsoft.UI.Xaml.Controls;

namespace Ripcord_App;

/// <summary>
/// A page's opinion about what should hold focus when it opens.
///
/// <para>
/// Without one, the first focus stop is whatever happens to be first in tree order — which on the console
/// list is the "Add console" button in the header, not the console the player came for.
/// </para>
///
/// <para>
/// A page knows which of its elements is the point of the page, and nothing above it does. Optional by design:
/// a page that does not care gets first-in-tree-order, which is the correct default for a settings or About
/// surface where no single control is the reason you are there.
/// </para>
/// </summary>
internal interface IInitialFocusTarget
{
    /// <summary>
    /// What to focus, or null when the page has nothing better to offer than tree order — including when it
    /// simply has not populated yet.
    /// </summary>
    Control? InitialFocus { get; }
}
