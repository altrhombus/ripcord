using System.ComponentModel;
using Ripcord.Presentation;
using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The contract every view-model in this layer inherits. Worth testing directly rather than only through a
/// concrete view-model: a fault here shows up as a stale binding in one page and a missing update in another,
/// which is the hardest kind of bug to attribute.
/// </summary>
public class ObservableStateTests
{
    private sealed record Snapshot(string Label, int Count);

    private sealed class Subject : ObservableState<Snapshot>
    {
        private string _label = "initial";
        private int _count;

        public Subject(IUiDispatcher dispatcher) : base(dispatcher) { }

        public int ComposeCount { get; private set; }

        public void SetLabel(string label) => Mutate(() => _label = label);

        public void SetCount(int count) => Mutate(() => _count = count);

        public void Bump() => Refresh();

        /// <summary>Reaches Mutate's null guard, which a subclass's own API would otherwise hide.</summary>
        public void MutateWithNull() => Mutate(null!);

        protected override Snapshot Compose()
        {
            ComposeCount++;
            return new Snapshot(_label, _count);
        }
    }

    private static Subject NewSubject() => new(new ImmediateUiDispatcher());

    [Fact]
    public void State_IsComposedLazily_SoASubclassCanFinishConstructing()
    {
        // Compose() must not run during the base constructor: a subclass's fields are not assigned yet at that
        // point, so an eager compose would read defaults and, worse, would do it only for the first value.
        var subject = NewSubject();
        Assert.Equal(0, subject.ComposeCount);

        _ = subject.State;

        Assert.Equal(1, subject.ComposeCount);
    }

    [Fact]
    public void Mutate_OnTheUiThread_IsSynchronous_SoACallerSeesItsOwnWrite()
    {
        // The property the IsOnUiThread fast path exists for. An event handler that calls a setter and then reads
        // the state on the next line must not observe the old value.
        var subject = NewSubject();

        subject.SetLabel("changed");

        Assert.Equal("changed", subject.State.Label);
    }

    [Fact]
    public void Mutate_RaisesPropertyChangedForStateOnly()
    {
        // XAML binds nested paths through this one property, so it is the only name that may ever be raised —
        // and raising a leaf name instead would silently fail to refresh anything.
        var subject = NewSubject();
        _ = subject.State;
        var raised = new List<string?>();
        ((INotifyPropertyChanged)subject).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        subject.SetLabel("changed");

        Assert.Equal(["State"], raised);
    }

    [Fact]
    public void Mutate_PublishesTheSameValueToBothChannels()
    {
        var subject = NewSubject();
        _ = subject.State;

        Snapshot? fromStream = null;
        using IDisposable subscription = subject.Changes.Subscribe(
            new DelegateObserver<Snapshot>(s => fromStream = s));

        Snapshot? fromProperty = null;
        ((INotifyPropertyChanged)subject).PropertyChanged += (_, _) => fromProperty = subject.State;

        subject.SetCount(7);

        Assert.NotNull(fromStream);
        Assert.Same(fromStream, fromProperty);   // one value, two channels — not two projections to keep in sync
    }

    [Fact]
    public void Mutate_ThatChangesNothing_NotifiesNobody()
    {
        // Record value equality is what makes this free. Without it, every idempotent setter and every Refresh()
        // would churn bindings, and the fix would be an early-return guard in each setter — which is the
        // per-property bookkeeping this design exists to delete.
        var subject = NewSubject();
        _ = subject.State;
        int notifications = 0;
        ((INotifyPropertyChanged)subject).PropertyChanged += (_, _) => notifications++;

        subject.SetLabel("initial");   // same value it already had
        subject.Bump();                // Refresh() with no field change

        Assert.Equal(0, notifications);
    }

    [Fact]
    public void State_IsReplacedWholesale_NotPatched()
    {
        // The core guarantee: a half-updated state is unrepresentable, so the "card reads Offline above a
        // Connect button" failure class cannot occur however many fields a future Compose() derives.
        var subject = NewSubject();
        Snapshot before = subject.State;

        subject.SetCount(3);

        Assert.NotSame(before, subject.State);
        Assert.Equal(0, before.Count);          // the old value is still intact
        Assert.Equal(3, subject.State.Count);
    }

    [Fact]
    public void Mutate_OffTheUiThread_IsPostedRatherThanAppliedInline()
    {
        var dispatcher = new DeferredDispatcher();
        var subject = new Subject(dispatcher);
        _ = subject.State;

        subject.SetLabel("changed");

        // Nothing applied yet — it is queued, exactly as a background discovery callback would be.
        Assert.Equal("initial", subject.State.Label);
        Assert.Single(dispatcher.Pending);

        dispatcher.Drain();

        Assert.Equal("changed", subject.State.Label);
    }

    [Fact]
    public void Constructor_RejectsANullDispatcher()
        => Assert.Throws<ArgumentNullException>(() => new Subject(null!));

    [Fact]
    public void Mutate_RejectsANullAction()
        => Assert.Throws<ArgumentNullException>(NewSubject().MutateWithNull);

    /// <summary>A dispatcher that is never "on the UI thread", so every mutation takes the posting path.</summary>
    private sealed class DeferredDispatcher : IUiDispatcher
    {
        public List<Action> Pending { get; } = [];

        public bool IsOnUiThread => false;

        public void Post(Action action) => Pending.Add(action);

        public void Drain()
        {
            foreach (Action action in Pending.ToList())
            {
                action();
            }

            Pending.Clear();
        }
    }

    private sealed class DelegateObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
