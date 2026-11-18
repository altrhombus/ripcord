namespace Ripcord.Presentation.Consoles;

/// <summary>
/// Which accent slot a surface carries. A semantic token, never a colour: the actual values live in each front
/// end's own palette — on Windows that is <c>Styles/Ripcord.xaml</c>, which in turn points at
/// <c>brand/README.md</c> — so the palette keeps exactly one home per platform and this layer stays free of UI
/// types.
///
/// <para>
/// Deliberately kept distinct from <see cref="ConsoleVendor"/> even though the two map one-to-one.
/// <c>ConsoleVendor</c> is a domain fact (who makes the console); this is a presentation slot (which accent to
/// draw with). Two vendors sharing a slot, or one vendor needing two, would make them diverge.
/// </para>
/// </summary>
public enum AccentRole
{
    PlayStation,
    Xbox,
    Nintendo,
}
