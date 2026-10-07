namespace Ripcord_App;

/// <summary>
/// A page with steps of its own, which Back should walk through before it leaves the page. The shell's Back (the
/// title bar's chevron, Escape, Alt+Left and the pad's Back) asks this first; false means the page is at its first
/// step, and the shell navigates back as usual.
/// </summary>
internal interface IStepBack
{
    bool TryStepBack();
}
