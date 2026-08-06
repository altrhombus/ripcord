using Ripcord.Core.Discovery;
using Ripcord.Core.Reactive;
using Ripcord.Presentation.Pairing;
using Ripcord.Protocol.Halyard.Common.Discovery;
using Ripcord.Protocol.Halyard.Discovery;

namespace Ripcord.Presentation.Halyard.Pairing;

/// <summary>
/// Searches every PlayStation family at once and merges the results into one stream.
///
/// <para>
/// One service per family is unavoidable: the two families listen on different ports with different protocol
/// versions (PS5 on 9302/00030010, PS4 on 987/00020020), so a single client can only ever see one of them. A
/// scan that used one client silently saw PS5 only — which is exactly the bug this shape exists to prevent.
/// </para>
///
/// <para>
/// Lives here rather than in <c>Ripcord.Protocol.Halyard</c> as an all-families
/// <c>IConsoleDiscoveryService</c>. That would arguably be a better home — it would put the merge where the
/// existing protocol test project could reach it without a new reference — and is worth doing if a second caller
/// ever appears. For one caller, an adapter in the adapter assembly is the smaller change.
/// </para>
/// </summary>
public sealed class HalyardConsoleScanner : IConsoleScanner
{
    public IObservable<DiscoveredConsole> Scan(TimeSpan window, CancellationToken cancellationToken)
        => new MergedFamilyScan(window, cancellationToken);

    private sealed class MergedFamilyScan(TimeSpan window, CancellationToken cancellationToken)
        : IObservable<DiscoveredConsole>
    {
        public IDisposable Subscribe(IObserver<DiscoveredConsole> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);

            var subscriptions = new List<IDisposable>();
            int outstanding = HalyardDiscoveryProfile.All.Count;
            var gate = new Lock();

            void OneFamilyDone()
            {
                bool last;
                lock (gate)
                {
                    last = --outstanding == 0;
                }

                // The merged stream completes when the last family does, not the first — otherwise a PS4 scan
                // that finishes early would cut off a PS5 still answering.
                if (last)
                {
                    observer.OnCompleted();
                }
            }

            foreach (HalyardDiscoveryProfile profile in HalyardDiscoveryProfile.All)
            {
                var service = new HalyardLanDiscoveryService(window, profile);
                subscriptions.Add(service.Discover(cancellationToken).Subscribe(
                    new AnonymousObserver<DiscoveredConsole>(
                        onNext: console =>
                        {
                            lock (gate)
                            {
                                observer.OnNext(console);
                            }
                        },

                        // One family failing to search must not sink the other: a PS4 probe on a machine where
                        // 987 is already taken should still leave the PS5 results standing. So an error counts as
                        // that family finishing, and is never forwarded to the merged observer.
                        onError: _ => OneFamilyDone(),
                        onCompleted: OneFamilyDone)));
            }

            return new CompositeSubscription(subscriptions);
        }
    }

    private sealed class CompositeSubscription(List<IDisposable> subscriptions) : IDisposable
    {
        public void Dispose()
        {
            foreach (IDisposable subscription in subscriptions)
            {
                subscription.Dispose();
            }

            subscriptions.Clear();
        }
    }
}
