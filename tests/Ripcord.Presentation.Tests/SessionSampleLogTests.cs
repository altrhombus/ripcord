using System.Globalization;
using Ripcord.Core.Sessions;
using Ripcord.Presentation.Sessions;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The session trace's format. Worth pinning because a trace is written to be read somewhere else, by
/// someone who was not there when it was taken.
/// </summary>
public class SessionSampleLogTests
{
    private static SessionSample Sample(
        double loss = 3.4, int receiveQueue = 18, double fps = 48, int decodeMode = 2)
        => new(
            ElapsedSeconds: 12.5,
            PresentFps: fps,
            DecodeFps: fps + 1,
            LossPercent: loss,
            RttMs: 34.2,
            BitrateMbps: 14.25,
            ReceiveQueueDepth: receiveQueue,
            DecodeQueueDepth: 1,
            PipelineLatencyMs: 22.4,
            DecodeMode: decodeMode,
            HealthLevel: StreamHealthLevel.Warning);

    [Fact]
    public void EveryHeaderColumnGetsExactlyOneValue()
    {
        // The failure this prevents is silent and total: a row with one field too many shifts every column
        // after it, and the numbers still look like numbers.
        int columns = SessionSampleLog.Header.Split(',').Length;
        int values = SessionSampleLog.Row(Sample()).Split(',').Length;

        Assert.Equal(columns, values);
    }

    [Fact]
    public void TheHeaderIsTheLastLineOfThePreamble()
    {
        // So a reader can skip '#' lines and land on the header without knowing how many there are.
        IReadOnlyList<string> preamble = SessionSampleLog.Preamble("1.0", "Test GPU", "HEVC", 1920, 1080, 60, 40_000);

        Assert.Equal(SessionSampleLog.Header, preamble[^1]);
        Assert.All(preamble.Take(preamble.Count - 1), line => Assert.StartsWith("#", line, StringComparison.Ordinal));
    }

    [Fact]
    public void ThePreambleCarriesTheThresholdsTheTraceIsBeingJudgedAgainst()
    {
        // A trace read months later should not need the reader to guess which build's constants produced it -
        // especially this one, which exists because a threshold is suspected of being wrong.
        string text = string.Join("\n", SessionSampleLog.Preamble("1.0", "Test GPU", "HEVC", 1920, 1080, 60, 40_000));

        Assert.Contains("receive_queue_busy=" + StreamHealthAssessor.ReceiveQueueBusyDepth, text, StringComparison.Ordinal);
        Assert.Contains("loss_warn=2%", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NumbersAreInvariantWhateverTheMachineSaysAboutDecimalPoints()
    {
        // A trace written on a comma-decimal machine and read on a dot-decimal one is a silent corruption -
        // and worse, every field would still parse as *something*, because the commas are the delimiter.
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            string row = SessionSampleLog.Row(Sample());

            Assert.Equal(SessionSampleLog.Header.Split(',').Length, row.Split(',').Length);
            Assert.Contains("14.25", row, StringComparison.Ordinal);
            Assert.DoesNotContain("14,25", row, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TheReceiveQueueSurvivesAsAnInteger()
    {
        // The whole point of the trace. If this column is ever rounded or formatted, the distribution it is
        // meant to answer becomes unreadable.
        Assert.Contains(",18,", SessionSampleLog.Row(Sample(receiveQueue: 18)), StringComparison.Ordinal);
        Assert.Contains(",0,", SessionSampleLog.Row(Sample(receiveQueue: 0)), StringComparison.Ordinal);
    }

    [Fact]
    public void DecodeModeDistinguishesTheDeviceStarvedRun()
    {
        // Run B forces software decode, so this column is how the three scenarios are told apart afterwards
        // without anyone having to label the files correctly at the time.
        Assert.EndsWith(",0,Warning", SessionSampleLog.Row(Sample(decodeMode: 0)), StringComparison.Ordinal);
        Assert.EndsWith(",2,Warning", SessionSampleLog.Row(Sample(decodeMode: 2)), StringComparison.Ordinal);
    }

    [Fact]
    public void ARowCarriesNothingThatIdentifiesAnyone()
    {
        // A trace is the thing most likely to be pasted into an issue. Measurements, not identity.
        string row = SessionSampleLog.Row(Sample());

        Assert.Matches(@"^[0-9.,\-]+,[A-Za-z]+$", row);
    }
}
