using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// Failures in plain words, with the raw text kept for a bug report. The raw texts here are the backends' own
/// wording, so a change there that breaks a match fails here.
/// </summary>
public class FailureCopyTests
{
    [Fact]
    public void Pairing_AWrongOrExpiredCode_SaysSoAndKeepsTheRawReason()
    {
        const string raw = "Registration was rejected by the console (HTTP 403, RP-Application-Reason 80108b09).";

        PlainFailure f = FailureCopy.ForPairing(raw);

        Assert.Contains("didn't accept that code", f.Message, StringComparison.Ordinal);
        Assert.Equal(raw, f.Technical);
    }

    [Theory]
    [InlineData("Registration was rejected by the console (HTTP 500).", "refused to pair")]
    [InlineData("Could not reach the console for registration: No route to host", "couldn't reach the console")]
    [InlineData("Couldn't reach PlayStation Network: timed out", "Check your internet connection")]
    public void Pairing_KnownFailures_LeadWithPlainWords(string raw, string expected)
        => Assert.Contains(expected, FailureCopy.ForPairing(raw).Message, StringComparison.Ordinal);

    [Fact]
    public void Pairing_AnAlreadyPlainReason_IsShownAsItIs()
    {
        PlainFailure f = FailureCopy.ForPairing("Sign in to your PlayStation Network account to pair without a code.");

        Assert.Equal("Sign in to your PlayStation Network account to pair without a code.", f.Message);
        Assert.Equal(string.Empty, f.Technical);
    }

    [Theory]
    [InlineData("/sess/init rejected (HTTP 403, RP-Application-Reason 80108b10).", "refused the connection")]
    [InlineData("Control setup timed out after 20s at: /sess/init. The console did not answer.", "didn't answer")]
    [InlineData("Stream key agreement did not complete (Takion/SESSION): Takion handshake: no INIT_ACK from the console", "signed in on the console")]
    [InlineData("Couldn't start a session: The console never joined the session, so it never got as far as publishing a registration seed.", "over the internet")]
    [InlineData("The console ended the session while it was starting.", "as it was starting")]
    public void Connect_KnownFailures_LeadWithPlainWords(string raw, string expected)
    {
        PlainFailure f = FailureCopy.ForConnect(raw);

        Assert.Contains(expected, f.Message, StringComparison.Ordinal);
        Assert.Equal(raw, f.Technical);
    }

    [Theory]
    [InlineData("Sign-in failed: the console rejected the passcode 5 times.")]
    [InlineData("This console was paired without an account, so the account service has no name for it.")]
    [InlineData("The console accepted 6 connections and dropped each one immediately. It may be going into rest mode — wake it and try again.")]
    public void Connect_AnAlreadyPlainReason_IsShownAsItIs(string raw)
    {
        PlainFailure f = FailureCopy.ForConnect(raw);

        Assert.Equal(raw, f.Message);
        Assert.Equal(string.Empty, f.Technical);
    }

    [Fact]
    public void Connect_AnythingElse_GetsTheGeneralSentence_AndKeepsTheRawText()
    {
        PlainFailure f = FailureCopy.ForConnect("Unexpected error: Object reference not set to an instance of an object.");

        Assert.Contains("couldn't connect to the console", f.Message, StringComparison.Ordinal);
        Assert.StartsWith("Unexpected error", f.Technical, StringComparison.Ordinal);
    }
}
