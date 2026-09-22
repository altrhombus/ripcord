using Ripcord.Core.Launch;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The command line, which is the whole of "launch Ripcord at a console".
///
/// <para>
/// These exist because the feature's entire purpose is unattended startup: every one of these cases arrives
/// from a shortcut, a launcher or a jump list that nobody is watching, and the alternative to a test is
/// discovering the rule was wrong when a game did not start.
/// </para>
/// </summary>
public class LaunchIntentTests
{
    [Fact]
    public void NoArguments_OpensTheShell()
    {
        Assert.Equal(LaunchAction.Shell, LaunchIntent.Parse([]).Action);
        Assert.Equal(LaunchAction.Shell, LaunchIntent.Parse(null).Action);
    }

    [Theory]
    [InlineData("--play")]
    [InlineData("-play")]
    [InlineData("/play")]
    public void PlayTakesItsTargetFromTheNextArgument(string flag)
    {
        // Three spellings because a shortcut written by hand uses whichever one its author is used to, and
        // refusing two of them buys nothing.
        LaunchIntent intent = LaunchIntent.Parse([flag, "Living room PS5"]);

        Assert.Equal(LaunchAction.Play, intent.Action);
        Assert.Equal("Living room PS5", intent.Target);
    }

    [Fact]
    public void PlayAlsoTakesItsTargetWithAnEqualsSign()
    {
        LaunchIntent intent = LaunchIntent.Parse(["--play=Living room PS5"]);

        Assert.Equal(LaunchAction.Play, intent.Action);
        Assert.Equal("Living room PS5", intent.Target);
    }

    [Fact]
    public void AQuotedTargetLosesItsQuotes()
    {
        // The shell usually strips these, but a jump list argument string written by hand may not.
        Assert.Equal("Living room PS5", LaunchIntent.Parse(["--play", "\"Living room PS5\""]).Target);
    }

    [Fact]
    public void PlayLastNeedsNoTarget()
    {
        LaunchIntent intent = LaunchIntent.Parse(["--play-last"]);

        Assert.Equal(LaunchAction.PlayLast, intent.Action);
        Assert.Null(intent.Target);
    }

    [Fact]
    public void PlayWithNothingAfterIt_OpensTheShell()
    {
        // A half-written shortcut. The shell is the honest answer: it shows every console, which is what the
        // person was reaching for.
        Assert.Equal(LaunchAction.Shell, LaunchIntent.Parse(["--play"]).Action);
        Assert.Equal(LaunchAction.Shell, LaunchIntent.Parse(["--play", "   "]).Action);
    }

    [Fact]
    public void AnUnrecognisedArgument_OpensTheShellRatherThanFailing()
    {
        // These arrive from shortcuts the app did not write and cannot fix, configured long ago. A typo
        // should cost one extra press, not an error in front of a game somebody was trying to start.
        Assert.Equal(LaunchAction.Shell, LaunchIntent.Parse(["--paly", "Living room PS5"]).Action);
        Assert.Equal(LaunchAction.Shell, LaunchIntent.Parse(["-- play"]).Action);
    }

    [Fact]
    public void ArgumentsBeforeTheFlagAreSkipped()
    {
        // A launcher may prepend its own. Only the flag we understand decides anything.
        LaunchIntent intent = LaunchIntent.Parse(["--from-launcher", "--play", "Bedroom"]);

        Assert.Equal(LaunchAction.Play, intent.Action);
        Assert.Equal("Bedroom", intent.Target);
    }

    [Fact]
    public void MatchesOnNameOrId_IgnoringCaseAndSpace()
    {
        LaunchIntent intent = LaunchIntent.Parse(["--play", "living room ps5"]);

        Assert.True(intent.Matches("Living Room PS5", "abc123"));
        Assert.True(intent.Matches(" Living Room PS5 ", null));
        Assert.False(intent.Matches("Bedroom PS5", "abc123"));
    }

    [Fact]
    public void MatchesOnTheId_SoARenamedConsoleKeepsWorking()
    {
        // The name in a shortcut is whatever it was when the shortcut was written. The id outlives a rename.
        LaunchIntent intent = LaunchIntent.Parse(["--play", "ABC123"]);

        Assert.True(intent.Matches("Some other name now", "abc123"));
    }

    [Fact]
    public void ShellAndPlayLastMatchNothing()
    {
        // Matches answers "is this the console asked for by name", and neither of these asked by name.
        Assert.False(LaunchIntent.Shell.Matches("Living room PS5", "abc"));
        Assert.False(LaunchIntent.Parse(["--play-last"]).Matches("Living room PS5", "abc"));
    }
}
