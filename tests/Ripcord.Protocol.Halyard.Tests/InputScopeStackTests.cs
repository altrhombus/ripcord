using Ripcord.Core.Input;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Who owns the controller. The ordering rules exist because the thing they replace — polling a page-owned
/// bool — was true during modals, which is the couch dead end.
/// </summary>
public class InputScopeStackTests
{
    /// <summary>Records its activation edges, since the edges are the whole mechanism.</summary>
    private sealed class Scope(InputScopeKind kind, string name) : IInputScope
    {
        public InputScopeKind Kind { get; } = kind;

        public List<string> Log { get; } = [];

        public bool IsActive { get; private set; }

        public void OnActivated()
        {
            IsActive = true;
            Log.Add("activated");
        }

        public void OnDeactivated()
        {
            IsActive = false;
            Log.Add("deactivated");
        }

        public override string ToString() => name;
    }

    [Fact]
    public void AnEmptyStackOwnsNothing()
    {
        var stack = new InputScopeStack();

        Assert.Null(stack.Top);
        Assert.False(stack.IsActive(InputScopeKind.Chrome));
    }

    [Fact]
    public void PushingActivatesAndDeactivatesTheOneBeneath()
    {
        var chrome = new Scope(InputScopeKind.Chrome, "chrome");
        var session = new Scope(InputScopeKind.Session, "session");
        var stack = new InputScopeStack();

        stack.Push(chrome);
        stack.Push(session);

        Assert.Same(session, stack.Top);
        Assert.True(session.IsActive);
        Assert.False(chrome.IsActive);
        Assert.Equal(["activated", "deactivated"], chrome.Log);
    }

    [Fact]
    public void TheOneBeneathIsDeactivatedBeforeTheNewOneActivates()
    {
        // Not cosmetic ordering: a Session's deactivate releases physically-held buttons into the console. If
        // the new scope activated first, two scopes would briefly both believe they owned the pad.
        List<string> order = [];
        var session = new OrderingScope(InputScopeKind.Session, "session", order);
        var modal = new OrderingScope(InputScopeKind.Modal, "modal", order);
        var stack = new InputScopeStack();

        stack.Push(session);
        order.Clear();
        stack.Push(modal);

        Assert.Equal(["session:deactivated", "modal:activated"], order);
    }

    [Fact]
    public void PoppingRestoresTheOneBeneath()
    {
        // The mid-session dialog case: dismissing it hands the pad back to the stream, which resumes forwarding.
        var session = new Scope(InputScopeKind.Session, "session");
        var modal = new Scope(InputScopeKind.Modal, "modal");
        var stack = new InputScopeStack();

        stack.Push(session);
        stack.Push(modal);
        stack.Pop(modal);

        Assert.Same(session, stack.Top);
        Assert.True(session.IsActive);
        Assert.False(modal.IsActive);
    }

    [Fact]
    public void ModalCoversSession_WhichIsTheWholePoint()
    {
        var session = new Scope(InputScopeKind.Session, "session");
        var modal = new Scope(InputScopeKind.Modal, "modal");
        var stack = new InputScopeStack();

        stack.Push(session);
        Assert.True(stack.IsActive(InputScopeKind.Session));

        stack.Push(modal);

        // Previously this was the broken state: the session still claimed the pad while a dialog was on screen,
        // so a controller-only user could not reach the dialog they had just opened.
        Assert.True(stack.IsActive(InputScopeKind.Modal));
        Assert.False(stack.IsActive(InputScopeKind.Session));
    }

    [Fact]
    public void PoppingFromUnderneathDisturbsNobody()
    {
        // Teardown order is not something a surface controls: a dialog can outlive the page that opened it
        // during a fast exit. Removing the buried claim must not steal the pad from whoever is on top.
        var chrome = new Scope(InputScopeKind.Chrome, "chrome");
        var session = new Scope(InputScopeKind.Session, "session");
        var modal = new Scope(InputScopeKind.Modal, "modal");
        var stack = new InputScopeStack();

        stack.Push(chrome);
        stack.Push(session);
        stack.Push(modal);

        session.Log.Clear();
        modal.Log.Clear();

        Assert.True(stack.Pop(session));

        Assert.Same(modal, stack.Top);
        Assert.True(modal.IsActive);
        Assert.Empty(modal.Log);
        Assert.Empty(session.Log);
    }

    [Fact]
    public void PoppingSomethingNeverPushedIsHarmless()
    {
        var stack = new InputScopeStack();
        stack.Push(new Scope(InputScopeKind.Chrome, "chrome"));

        Assert.False(stack.Pop(new Scope(InputScopeKind.Modal, "stranger")));
        Assert.Equal(1, stack.Count);
    }

    [Fact]
    public void PushingTheSameScopeTwiceThrows()
    {
        // A surface that pushes twice has a lifecycle bug, and absorbing it would make the matching pop
        // ambiguous — the second pop would silently release a claim that is still meant to be held.
        var scope = new Scope(InputScopeKind.Chrome, "chrome");
        var stack = new InputScopeStack();
        stack.Push(scope);

        Assert.Throws<InvalidOperationException>(() => stack.Push(scope));
    }

    [Fact]
    public void TopChanged_FiresOnEveryEdgeAndCarriesTheNewTop()
    {
        var chrome = new Scope(InputScopeKind.Chrome, "chrome");
        var modal = new Scope(InputScopeKind.Modal, "modal");
        var stack = new InputScopeStack();

        List<IInputScope?> observed = [];
        stack.TopChanged += observed.Add;

        stack.Push(chrome);
        stack.Push(modal);
        stack.Pop(modal);
        stack.Pop(chrome);

        Assert.Equal([chrome, modal, chrome, null], observed);
    }

    private sealed class OrderingScope(InputScopeKind kind, string name, List<string> log) : IInputScope
    {
        public InputScopeKind Kind { get; } = kind;

        public void OnActivated() => log.Add($"{name}:activated");

        public void OnDeactivated() => log.Add($"{name}:deactivated");
    }
}
