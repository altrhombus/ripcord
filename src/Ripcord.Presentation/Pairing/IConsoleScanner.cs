using Ripcord.Core.Discovery;

namespace Ripcord.Presentation.Pairing;

/// <summary>
/// Searches the network for consoles, publishing each one the moment it answers.
///
/// <para>
/// A stream rather than a list, because the flow shows results as they arrive: the search window has to be
/// generous enough for a resting console, which answers more slowly than an awake one, and a four-second wait
/// that shows nothing until it elapses feels longer than one that fills in as it goes.
/// </para>
///
/// <para>
/// Scanning <em>every</em> family rather than only the chosen one is the implementation's business, but it is
/// the intended behaviour: a search client built with no profile silently defaults to PS5, and a user who
/// picks the wrong family is better served by seeing their actual console than an empty list.
/// </para>
/// </summary>
public interface IConsoleScanner
{
    IObservable<DiscoveredConsole> Scan(TimeSpan window, CancellationToken cancellationToken);
}
