using Ripcord.Core.Consoles;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Threading;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The console card. The point of most of these is that the derived values are asserted <em>together</em>: the
/// hand-rolled view-model this replaces raised eleven property names from one setter and warned in a comment that
/// missing one "leaves a card reading 'Offline' above a 'Connect' button". A test per reachability that checks the
/// label, the action, the glyph, the tone and connectability in one go is the direct answer to that.
/// </summary>
public class ConsoleCardViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 5, 12, 0, 0, TimeSpan.Zero);

    private static PairedConsole Console(
        string platform = "Ps5",
        string? nickname = null,
        DateTimeOffset? lastConnected = null) =>
        new(Id: "host-id", Name: "PlayStation 5", Host: "10.0.0.7", Platform: platform, CredentialBlob: "")
        {
            Nickname = nickname,
            LastConnectedUtc = lastConnected,
        };

    private static ConsoleCardViewModel NewCard(PairedConsole? console = null) =>
        new(console ?? Console(), new ImmediateUiDispatcher(), () => Now);

    [Fact]
    public void StartsAsChecking_SoARowNeverClaimsToKnowBeforeItsProbeResolves()
    {
        ConsoleCardState state = NewCard().State;

        Assert.Equal(ConsoleReachability.Checking, state.Reachability);
        Assert.True(state.IsChecking);
        Assert.Equal("Checking…", state.StatusLabel);
        Assert.Equal(StatusTone.Unknown, state.StatusTone);
    }

    [Fact]
    public void Online_IsReadyAndConnectable()
    {
        var card = NewCard();
        card.Reachability = ConsoleReachability.Online;

        ConsoleCardState s = card.State;
        Assert.Equal("Ready", s.StatusLabel);
        Assert.Equal(StatusTone.Positive, s.StatusTone);
        Assert.Equal("Play", s.PrimaryActionLabel);
        Assert.Equal(ActionGlyph.Play, s.ActionGlyph);
        Assert.True(s.CanConnect);
        Assert.False(s.IsChecking);
    }

    [Theory]
    [InlineData(ConsoleReachability.Resting, "Rest mode")]
    [InlineData(ConsoleReachability.PreparingForRest, "Going to sleep…")]
    public void Resting_PromisesTheWakeRatherThanDemandingItFirst(ConsoleReachability reachability, string label)
    {
        // A resting console is not a problem to solve before connecting — connecting wakes it — so the action
        // promises both. Both resting states share a tone so the dot does not jump hue when it settles.
        var card = NewCard();
        card.Reachability = reachability;

        ConsoleCardState s = card.State;
        Assert.Equal(label, s.StatusLabel);
        Assert.Equal(StatusTone.Caution, s.StatusTone);
        Assert.Equal("Wake & play", s.PrimaryActionLabel);
        Assert.Equal(ActionGlyph.Wake, s.ActionGlyph);
        Assert.True(s.CanConnect);
    }

    [Fact]
    public void Offline_IsNeutralNotCritical_AndKeepsThePlayGlyph()
    {
        // A powered-off console is a normal state, not an error: a Critical tone here would cry wolf every time
        // someone turns their console off. The glyph stays Play because the card is already dimmed and already
        // says "Can't reach it" — an error icon would add a third claim of fault to a non-fault.
        var card = NewCard();
        card.Reachability = ConsoleReachability.Offline;

        ConsoleCardState s = card.State;
        Assert.Equal("Offline", s.StatusLabel);
        Assert.Equal(StatusTone.Neutral, s.StatusTone);
        Assert.Equal("Can't reach it", s.PrimaryActionLabel);
        Assert.Equal(ActionGlyph.Play, s.ActionGlyph);
        Assert.False(s.CanConnect);
    }

    [Fact]
    public void StatusAndActionAlwaysAgree_ForEveryReachability()
    {
        // The failure the old cascade risked, asserted structurally rather than case by case: it must never be
        // possible to read a "cannot" status beside a "can" action.
        foreach (ConsoleReachability reachability in Enum.GetValues<ConsoleReachability>())
        {
            var card = NewCard();
            card.Reachability = reachability;
            ConsoleCardState s = card.State;

            // "play" rather than "connect" since the labels took the player's verb: Play / Wake & play, against
            // "Can't reach it" for the one state that offers nothing.
            bool actionOffersPlay = s.PrimaryActionLabel.Contains("play", StringComparison.OrdinalIgnoreCase);
            Assert.Equal(s.CanConnect, actionOffersPlay);
        }
    }

    [Fact]
    public void Details_LeadWithTheFamily()
        => Assert.Equal("PS5 · 10.0.0.7", NewCard().State.Details);

    [Fact]
    public void Accent_ComesFromTheConsolesOwnFamily()
    {
        // A PS4 record must not be drawn with, or described as, a PS5.
        var ps4 = NewCard(Console(platform: "Ps4"));
        Assert.Equal(AccentRole.PlayStation, ps4.State.Accent);
        Assert.StartsWith("PS4 · ", ps4.State.Details);
    }

    [Fact]
    public void AutomationName_IsOneSentence_NotFourFragments()
    {
        var card = NewCard(Console(nickname: "Front room"));
        card.Reachability = ConsoleReachability.Resting;

        Assert.Equal("Front room, PS5, Rest mode. Wake & play", card.State.AutomationName);
    }

    [Fact]
    public void AutomationName_TracksStatus_SoAScreenReaderIsNeverStale()
    {
        var card = NewCard();
        card.Reachability = ConsoleReachability.Online;
        Assert.Contains("Ready", card.State.AutomationName);

        card.Reachability = ConsoleReachability.Offline;
        Assert.Contains("Offline", card.State.AutomationName);
    }

    [Fact]
    public void LastConnected_IsAbsentUntilThereIsOne()
    {
        Assert.Null(NewCard().State.LastConnectedLabel);
        Assert.False(NewCard().State.HasLastConnected);

        var played = NewCard(Console(lastConnected: Now.AddMinutes(-30)));
        Assert.Equal("Played 30 min ago", played.State.LastConnectedLabel);
        Assert.True(played.State.HasLastConnected);
    }

    [Fact]
    public void Update_ReplacesTheRecordInPlace_AndEverythingDerivedFollows()
    {
        // In place, because rebuilding the list would destroy focus — and this grid is navigated with a gamepad.
        var card = NewCard();
        card.Reachability = ConsoleReachability.Online;

        card.Update(Console(nickname: "Front room", lastConnected: Now.AddHours(-2)));

        ConsoleCardState s = card.State;
        Assert.Equal("Front room", s.DisplayName);
        Assert.Equal("Played 2 hours ago", s.LastConnectedLabel);
        Assert.Contains("Front room", s.AutomationName);

        // The live status is not part of the stored record, so a record swap must not reset it.
        Assert.Equal(ConsoleReachability.Online, s.Reachability);
    }

    [Fact]
    public void Highlight_IsPortableTruth_NotAnOpacity()
    {
        // How strongly a hover reads is a styling decision and belongs in the front end. This layer only says
        // whether the card is highlighted.
        var card = NewCard();
        Assert.False(card.State.IsHighlighted);

        card.IsHighlighted = true;
        Assert.True(card.State.IsHighlighted);
    }

    [Fact]
    public void SettingTheSameReachabilityTwice_ProducesNoNewState()
    {
        var card = NewCard();
        card.Reachability = ConsoleReachability.Online;
        ConsoleCardState first = card.State;

        card.Reachability = ConsoleReachability.Online;

        Assert.Same(first, card.State);
    }

    [Fact]
    public void RefreshElapsed_RecomposesWithoutAFieldChange()
    {
        // The last-played caption goes stale on its own as the clock moves, with no field to change.
        DateTimeOffset now = Now;
        var card = new ConsoleCardViewModel(
            Console(lastConnected: Now.AddMinutes(-1)), new ImmediateUiDispatcher(), () => now);

        Assert.Equal("Played just now", card.State.LastConnectedLabel);

        now = Now.AddMinutes(30);
        card.RefreshElapsed();

        Assert.Equal("Played 31 min ago", card.State.LastConnectedLabel);
    }

    [Fact]
    public void Constructor_RejectsANullConsole()
        => Assert.Throws<ArgumentNullException>(
            () => new ConsoleCardViewModel(null!, new ImmediateUiDispatcher()));
}
