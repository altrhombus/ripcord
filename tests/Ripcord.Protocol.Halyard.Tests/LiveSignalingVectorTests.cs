using Ripcord.Cloud.Halyard;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Validates <see cref="HalyardSignalingMessage.TryParse"/> against the real captured push frames — the
/// decoded WebSocket dump from cap66/cap67, held in the dirty room.
///
/// <para>
/// This is what turns the parser from "handles the shapes I wrote" into "handles what the console actually
/// sends". The synthetic tests in <see cref="HalyardSignalingMessageTests"/> pin the contract; this one pins
/// it to ground truth, and would catch a shape the synthetic frames simplified away. Skips when the dirty-room
/// dump is absent, so CI and a clean checkout stay green.
/// </para>
///
/// <para>
/// Fixture: <c>docs/protocol/captures/ws_frames.txt</c> — the raw <c>dump_ws.py</c> output (one frame per
/// line, direction-prefixed). Gitignored.
/// </para>
/// </summary>
public class LiveSignalingVectorTests
{
    [SkippableFact]
    public void RealPushFrames_YieldTheConsolesCandidates()
    {
        string[] frames = LoadFramesOrSkip();

        var consoleOffers = new List<HalyardSignalingMessage>();
        foreach (string frame in frames)
        {
            HalyardSignalingMessage? msg = HalyardSignalingMessage.TryParse(frame);
            if (msg is { IsConsoleOffer: true })
            {
                consoleOffers.Add(msg);
            }
        }

        // The capture contains at least one console OFFER (PS5 and PS4 sessions were both recorded).
        Assert.NotEmpty(consoleOffers);

        foreach (HalyardSignalingMessage offer in consoleOffers)
        {
            // Every console OFFER advertises reachable candidates on the account-route port, and carries the
            // populated skey/hashedId the console fills in (16B / 20B) — the facts the decode established.
            Assert.NotEmpty(offer.Candidates);
            Assert.Contains(offer.Candidates, c => c.Port == 9303);
            Assert.All(offer.Candidates, c =>
            {
                Assert.False(string.IsNullOrWhiteSpace(c.Address));
                Assert.False(string.IsNullOrWhiteSpace(c.Type));
            });

            Assert.NotNull(offer.SessionKey);
            Assert.Equal(16, offer.SessionKey!.Length);
            Assert.NotNull(offer.LocalHashedId);
            Assert.Equal(20, offer.LocalHashedId!.Length);
        }
    }

    [SkippableFact]
    public void EveryFrameParsesWithoutThrowing_AndNonSignalingOnesAreNull()
    {
        // The dump is mostly presence/membership/customData notifications with a few signaling messages. The
        // parser must treat the whole stream as ordinary input: signaling frames parse, everything else is
        // null, nothing throws.
        string[] frames = LoadFramesOrSkip();

        int signaling = 0;
        foreach (string frame in frames)
        {
            HalyardSignalingMessage? msg = HalyardSignalingMessage.TryParse(frame);
            if (msg is not null)
            {
                signaling++;
                Assert.False(string.IsNullOrEmpty(msg.Action));
            }
        }

        Assert.True(signaling > 0, "Expected at least one signaling frame in the capture.");
    }

    [SkippableFact]
    public async Task PushChannel_ReplayingRealFrames_SurfacesTheConsoleOffer()
    {
        // End-to-end on ground truth: the real captured frames pushed through HalyardPushChannel's dispatch
        // loop (via a fake socket) must yield the console's OFFER with candidates — proving the channel, not
        // just the parser, handles what the console actually sends.
        string[] frames = LoadFramesOrSkip();

        var socket = new ReplayWebSocket(frames);
        var offers = new List<Cloud.Halyard.HalyardSignalingMessage>();
        await using var channel = new Cloud.Halyard.HalyardPushChannel(socket);
        channel.SignalingReceived += m =>
        {
            if (m.IsConsoleOffer)
            {
                offers.Add(m);
            }
        };

        var server = new Cloud.Halyard.HalyardPushServerInfo("host", null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await channel.RunAsync(server, "token", cts.Token);

        Assert.NotEmpty(offers);
        Assert.All(offers, o => Assert.NotEmpty(o.Candidates));
    }

    /// <summary>A fake socket that replays a fixed set of frames then closes.</summary>
    private sealed class ReplayWebSocket(IReadOnlyList<string> frames) : Ripcord.Core.Net.WebSockets.IWebSocketChannel
    {
        private int _index;

        public Task ConnectAsync(
            Uri uri, IReadOnlyDictionary<string, string> headers, TimeSpan keepAlive, CancellationToken ct)
            => Task.CompletedTask;

        public Task<string?> ReceiveAsync(CancellationToken ct)
            => Task.FromResult(_index < frames.Count ? frames[_index++] : null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string[] LoadFramesOrSkip()
    {
        string path = Locate();
        Skip.IfNot(File.Exists(path), $"Dirty-room push dump not present ({path}).");

        // dump_ws.py prefixes each frame with ">>> " / "<<< "; strip that and keep non-empty JSON lines.
        return [.. File.ReadAllLines(path)
            .Select(l => l.StartsWith(">>> ", StringComparison.Ordinal) || l.StartsWith("<<< ", StringComparison.Ordinal)
                ? l[4..]
                : l)
            .Where(l => l.TrimStart().StartsWith('{'))];
    }

    private static string Locate()
    {
        string? dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++)
        {
            string candidate = Path.Combine(dir, "docs", "protocol", "captures", "ws_frames.txt");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return Path.Combine(AppContext.BaseDirectory, "ws_frames.txt");
    }
}
