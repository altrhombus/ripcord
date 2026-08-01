using System.Text.Json;
using Ripcord.Protocol.Halyard.Session;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The <c>streamResolutions</c> ladder sent in the launchSpec.
///
/// <para>
/// We previously sent a single entry at score 10, which left the console no fallback if it would not serve that
/// resolution and expressed no ordered preference. The captured vendor launchSpec advertises four rungs with
/// ascending scores, so these tests pin our shape against the vendor's rather than against an assumption:
/// scores ascend, the requested resolution is always the most preferred, and no rung above the request is
/// offered (offering one would invite the console to send more than was asked for).
/// </para>
/// </summary>
public class LaunchSpecResolutionLadderTests
{
    private static JsonElement Parse(int w, int h, int fps) =>
        JsonDocument.Parse("[" + HalyardStreamingSession.BuildStreamResolutions(w, h, fps) + "]").RootElement;

    private static (int W, int H, int Fps, int Score) Rung(JsonElement e) =>
        (e.GetProperty("resolution").GetProperty("width").GetInt32(),
         e.GetProperty("resolution").GetProperty("height").GetInt32(),
         e.GetProperty("maxFps").GetInt32(),
         e.GetProperty("score").GetInt32());

    [Fact]
    public void RequestingTheTopRung_ReproducesTheVendorLadder()
    {
        // Exactly what the captured vendor launchSpec advertises, scores included.
        JsonElement ladder = Parse(1920, 1080, 60);

        Assert.Equal(4, ladder.GetArrayLength());
        Assert.Equal((640, 360, 60, 1), Rung(ladder[0]));
        Assert.Equal((960, 540, 60, 2), Rung(ladder[1]));
        Assert.Equal((1280, 720, 60, 3), Rung(ladder[2]));
        Assert.Equal((1920, 1080, 60, 4), Rung(ladder[3]));
    }

    [Fact]
    public void TheRequestedResolutionAlwaysScoresHighest()
    {
        // The property that keeps added fallbacks from becoming a downgrade.
        foreach ((int w, int h) in new[] { (640, 360), (960, 540), (1280, 720), (1920, 1080) })
        {
            JsonElement ladder = Parse(w, h, 60);
            var last = Rung(ladder[ladder.GetArrayLength() - 1]);

            Assert.Equal((w, h), (last.W, last.H));
            for (int i = 0; i < ladder.GetArrayLength(); i++)
            {
                Assert.True(Rung(ladder[i]).Score <= last.Score);
            }
        }
    }

    [Fact]
    public void NoRungTallerThanTheRequestIsOffered()
    {
        JsonElement ladder = Parse(1280, 720, 60);

        Assert.Equal(3, ladder.GetArrayLength());
        for (int i = 0; i < ladder.GetArrayLength(); i++)
        {
            Assert.True(Rung(ladder[i]).H <= 720);
        }
    }

    [Fact]
    public void ScoresAscendContiguouslyFromOne()
    {
        JsonElement ladder = Parse(1920, 1080, 60);

        for (int i = 0; i < ladder.GetArrayLength(); i++)
        {
            Assert.Equal(i + 1, Rung(ladder[i]).Score);
        }
    }

    [Fact]
    public void TheLowestRungStillYieldsASingleEntry()
    {
        // Nothing below 360p, so the ladder collapses to the request alone rather than emitting an empty array
        // (which the console would reject along with the rest of an incomplete launchSpec).
        JsonElement ladder = Parse(640, 360, 60);

        Assert.Equal(1, ladder.GetArrayLength());
        Assert.Equal((640, 360, 60, 1), Rung(ladder[0]));
    }

    [Fact]
    public void ANonStandardRequestKeepsItsFallbacksAndTheTopScore()
    {
        // The adaptive ladder can ask for sizes that are not vendor rungs; such a request must still be offered
        // last (most preferred) and still carry the rungs below it.
        JsonElement ladder = Parse(1600, 900, 60);

        Assert.Equal(4, ladder.GetArrayLength());
        Assert.Equal((1600, 900, 60, 4), Rung(ladder[3]));
        Assert.Equal((1280, 720, 60, 3), Rung(ladder[2]));
    }

    [Fact]
    public void TheRequestedFrameRateIsAppliedToEveryRung()
    {
        JsonElement ladder = Parse(1920, 1080, 30);

        for (int i = 0; i < ladder.GetArrayLength(); i++)
        {
            Assert.Equal(30, Rung(ladder[i]).Fps);
        }
    }

    [Fact]
    public void TheRequestIsNotDuplicatedWhenItMatchesAStandardRung()
    {
        // 1080p is itself a standard rung; it must appear once, not twice.
        JsonElement ladder = Parse(1920, 1080, 60);

        int matches = 0;
        for (int i = 0; i < ladder.GetArrayLength(); i++)
        {
            if (Rung(ladder[i]) is { W: 1920, H: 1080 })
            {
                matches++;
            }
        }

        Assert.Equal(1, matches);
    }
}
