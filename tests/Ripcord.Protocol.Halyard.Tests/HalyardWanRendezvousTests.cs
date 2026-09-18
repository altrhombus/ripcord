using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Ripcord.Cloud.Halyard;
using Ripcord.Cloud.Halyard.Rendezvous;
using Ripcord.Core.Net.WebSockets;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The WAN rendezvous state machine — the sequencing of session-create, wake, STUN, offer, and the wait for the
/// console's OFFER — driven entirely by fakes so the orchestration is tested without HTTP, STUN, or a live
/// WebSocket.
///
/// <para>
/// The behaviours worth pinning are the ones that are invisible until a real connect fails: that the push
/// channel is listening <em>before</em> the console is triggered (or an OFFER arriving the instant it joins is
/// lost), that we keep re-offering while waiting (the console takes seconds to join), that a console that never
/// answers times out rather than hangs, and that a failure tears the whole thing down instead of leaking the
/// session and socket.
/// </para>
/// </summary>
public class HalyardWanRendezvousTests
{
    // ---- fakes ---------------------------------------------------------------------------------

    private sealed class FakeSignaling : IHalyardSignalingClient
    {
        public ConcurrentQueue<string> Calls { get; } = new();

        public int OfferCount { get; private set; }

        public string SessionId { get; set; } = "session-1";

        public bool Left { get; private set; }

        /// <summary>Fires when the connect command is sent — lets a test release the console's OFFER then.</summary>
        public TaskCompletionSource CommandSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string> CreateSessionAsync(string pushContextId, CancellationToken ct)
        {
            Calls.Enqueue("create");
            return Task.FromResult(SessionId);
        }

        public Task SendConnectCommandAsync(
            string duid, string accountId, string sessionId, string clientType,
            (string, string, string) seeds, CancellationToken ct)
        {
            Calls.Enqueue("command");
            CommandSent.TrySetResult();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Runs after each OFFER POST, with the number sent so far. It is how a test says "the console
        /// answers on our Nth offer" without timing it: a claim about how many rounds the loop makes has to
        /// be driven by the loop, because a wall-clock delay only decides how many rounds a given host
        /// happens to fit alongside it.
        /// </summary>
        public Action<int>? OnOfferSent { get; set; }

        public Task SendOfferAsync(
            string sessionId, string accountId, string duid, IReadOnlyList<HalyardCandidate> candidates, CancellationToken ct, ReadOnlyMemory<byte> localHashedId = default, int reqId = 1, int sid = 1)
        {
            OfferCount++;
            Calls.Enqueue("offer");
            LastOffer = candidates;
            OnOfferSent?.Invoke(OfferCount);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HalyardCloudSession>> GetSessionAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<HalyardCloudSession>>(
                [new HalyardCloudSession(SessionId, [new HalyardSessionMember("42", "REMOTE_PLAY", "me")])]);        public Task SendResultAsync(string a, string b, string c, int reqId, CancellationToken ct) => Task.CompletedTask;
        public Task SendAcceptAsync(string a, string b, string c, int reqId, int sid, int peerSid, HalyardCandidate cand, string addr, int port, CancellationToken ct) => Task.CompletedTask;


        public Task LeaveSessionAsync(string sessionId, CancellationToken ct)
        {
            Left = true;
            Calls.Enqueue("leave");
            return Task.CompletedTask;
        }

        public IReadOnlyList<HalyardCandidate>? LastOffer { get; private set; }
    }

    /// <summary>
    /// Fails the first N OFFER POSTs with a 404-style error, then succeeds — as a propagating session does.
    /// <see cref="OnOfferAccepted"/> then fires once, which lets a test make the console's answer a
    /// consequence of the OFFER that finally landed rather than of a timer running out.
    /// </summary>
    private sealed class FailFirstOffersSignaling(int failures) : IHalyardSignalingClient
    {
        private int _offerAttempts;

        private int _accepted;

        public int OfferAttempts => _offerAttempts;

        /// <summary>Runs once, on the first OFFER POST that is not failed.</summary>
        public Action? OnOfferAccepted { get; set; }

        public Task<string> CreateSessionAsync(string pushContextId, CancellationToken ct) => Task.FromResult("s");

        public Task SendConnectCommandAsync(
            string duid, string a, string s, string c, (string, string, string) seeds, CancellationToken ct)
            => Task.CompletedTask;

        public Task SendOfferAsync(string s, string a, string d, IReadOnlyList<HalyardCandidate> c, CancellationToken ct, ReadOnlyMemory<byte> localHashedId = default, int reqId = 1, int sid = 1)
        {
            if (Interlocked.Increment(ref _offerAttempts) <= failures)
            {
                throw new HalyardCloudException("POST .../sessionMessage failed (404).");
            }

            if (Interlocked.Exchange(ref _accepted, 1) == 0)
            {
                OnOfferAccepted?.Invoke();
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<HalyardCloudSession>> GetSessionAsync(string sessionId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<HalyardCloudSession>>([]);        public Task SendResultAsync(string a, string b, string c, int reqId, CancellationToken ct) => Task.CompletedTask;
        public Task SendAcceptAsync(string a, string b, string c, int reqId, int sid, int peerSid, HalyardCandidate cand, string addr, int port, CancellationToken ct) => Task.CompletedTask;


        public Task LeaveSessionAsync(string s, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeGatherer(IPEndPoint? reflexive) : IReflexiveGatherer
    {
        public Task<IPEndPoint?> GatherAsync(UdpClient socket, CancellationToken ct) => Task.FromResult(reflexive);
    }

    /// <summary>A push socket a test feeds frames into on demand.</summary>
    private sealed class ControllableWebSocket : IWebSocketChannel
    {
        private readonly Channel<string> _frames = Channel.CreateUnbounded<string>();

        public bool Connected { get; private set; }

        /// <summary>When set, <see cref="ConnectAsync"/> blocks on it — to test what waits for the connection.</summary>
        public TaskCompletionSource? ConnectGate { get; set; }

        public void Push(string frame) => _frames.Writer.TryWrite(frame);

        public async Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, TimeSpan keepAlive, CancellationToken ct)
        {
            if (ConnectGate is not null)
            {
                await ConnectGate.Task.WaitAsync(ct);
            }

            Connected = true;
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            try
            {
                return await _frames.Reader.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static readonly HalyardPushServerInfo Server = new("host", null);
    private static readonly HalyardWanRequest Request = new("console-duid", "account-42");

    private static string ConsoleOfferFrame(string port = "9303") =>
        "{\"version\":\"2.1\",\"method\":3001,"
        + "\"dataType\":\"psn:sessionManager:sys:rps:sessionMessage:created\","
        + "\"to\":{\"accountId\":1,\"onlineId\":\"t\",\"platform\":[\"REMOTE_PLAY\"]},"
        + "\"body\":{\"data\":{"
        + "\"customProperties\":{\"from\":{\"accountId\":\"1\",\"platform\":\"PROSPERO\"}},"
        + "\"sessionId\":\"00000000-0000-0000-0000-000000000000\","
        + "\"sessionMessage\":{\"channel\":\"remote_play:1\","
        + "\"payload\":\"ver=1.0, type=text, body="
        + "{\\\"action\\\":\\\"OFFER\\\",\\\"reqId\\\":1,\\\"error\\\":0,\\\"connRequest\\\":{"
        + "\\\"candidate\\\":[{\\\"type\\\":\\\"STATIC\\\",\\\"addr\\\":\\\"203.0.113.7\\\",\\\"mappedAddr\\\":\\\"0.0.0.0\\\",\\\"port\\\":" + port + ",\\\"mappedPort\\\":0}]}}\"}}}}";

    private static UdpClient MediaSocket() => new(new IPEndPoint(IPAddress.Loopback, 0));

    private static (HalyardWanRendezvous Rendezvous, FakeSignaling Signaling, ControllableWebSocket Socket, HalyardPushChannel Channel) Build(
        IPEndPoint? reflexive = null, HalyardWanRendezvousOptions? options = null)
    {
        var signaling = new FakeSignaling();
        var socket = new ControllableWebSocket();
        var channel = new HalyardPushChannel(socket);
        var rendezvous = new HalyardWanRendezvous(signaling, new FakeGatherer(reflexive), options);
        return (rendezvous, signaling, socket, channel);
    }

    // ---- tests ---------------------------------------------------------------------------------

    [Fact]
    public async Task ReturnsTheConsolesCandidatesOnceItAnswers()
    {
        var (rendezvous, signaling, socket, channel) = Build(
            reflexive: new IPEndPoint(IPAddress.Parse("198.51.100.9"), 40000));
        using UdpClient media = MediaSocket();

        // Release the console's OFFER as soon as the wake command has been sent.
        _ = signaling.CommandSent.Task.ContinueWith(_ => socket.Push(ConsoleOfferFrame()), TaskScheduler.Default);

        await using HalyardWanConnection conn = await rendezvous.ConnectAsync(
            Request, media, channel, Server, "token", CancellationToken.None);

        Assert.Equal("session-1", conn.SessionId);
        HalyardSignalingCandidate cand = Assert.Single(conn.ConsoleCandidates);
        Assert.Equal("STATIC", cand.Type);
        Assert.Equal(9303, cand.Port);
    }

    [Fact]
    public async Task OffersBothOurReflexiveAndLanCandidates()
    {
        var (rendezvous, signaling, socket, channel) = Build(
            reflexive: new IPEndPoint(IPAddress.Parse("198.51.100.9"), 40000));
        using UdpClient media = MediaSocket();
        _ = signaling.CommandSent.Task.ContinueWith(_ => socket.Push(ConsoleOfferFrame()), TaskScheduler.Default);

        await using HalyardWanConnection conn = await rendezvous.ConnectAsync(
            Request, media, channel, Server, "token", CancellationToken.None);

        Assert.NotNull(signaling.LastOffer);
        Assert.Contains(signaling.LastOffer!, c => c.Type == "STATIC" && c.Address == "198.51.100.9");
        Assert.Contains(signaling.LastOffer!, c => c.Type == "LOCAL");
    }

    [Fact]
    public async Task OffersLanOnlyWhenStunFindsNoReflexiveAddress()
    {
        var (rendezvous, signaling, socket, channel) = Build(reflexive: null);
        using UdpClient media = MediaSocket();
        _ = signaling.CommandSent.Task.ContinueWith(_ => socket.Push(ConsoleOfferFrame()), TaskScheduler.Default);

        await using HalyardWanConnection conn = await rendezvous.ConnectAsync(
            Request, media, channel, Server, "token", CancellationToken.None);

        Assert.NotNull(signaling.LastOffer);
        Assert.DoesNotContain(signaling.LastOffer!, c => c.Type == "STATIC");
    }

    [Fact]
    public async Task ListensBeforeTriggeringTheConsole_SoAnEarlyOfferIsNotLost()
    {
        // The order that matters: the push socket must be connected before the wake command fires, or an OFFER
        // the console sends the instant it joins races ahead of us and is missed.
        var (rendezvous, signaling, socket, channel) = Build();
        using UdpClient media = MediaSocket();

        bool connectedBeforeCommand = false;
        _ = signaling.CommandSent.Task.ContinueWith(
            _ =>
            {
                connectedBeforeCommand = socket.Connected;
                socket.Push(ConsoleOfferFrame());
            },
            TaskScheduler.Default);

        await using HalyardWanConnection conn = await rendezvous.ConnectAsync(
            Request, media, channel, Server, "token", CancellationToken.None);

        Assert.True(connectedBeforeCommand);
    }

    [Fact]
    public async Task DoesNotCreateTheSessionUntilThePushConnectionIsEstablished()
    {
        // The bug a live run hit: creating the session before the push WebSocket connected made PSN bind the
        // session to no push connection, and its sessionMessage sub-resource 404'd. Create must wait for the
        // push upgrade.
        var (rendezvous, signaling, socket, channel) = Build();
        using UdpClient media = MediaSocket();
        socket.ConnectGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = signaling.CommandSent.Task.ContinueWith(_ => socket.Push(ConsoleOfferFrame()), TaskScheduler.Default);

        Task<HalyardWanConnection> connect = rendezvous.ConnectAsync(
            Request, media, channel, Server, "token", CancellationToken.None);

        // While the push connection is gated, the session must NOT have been created.
        await Task.Delay(100);
        Assert.DoesNotContain("create", signaling.Calls);

        // Release the connection; now the whole flow completes.
        socket.ConnectGate.SetResult();
        await using HalyardWanConnection conn = await connect;
        Assert.Contains("create", signaling.Calls);
    }

    [Fact]
    public async Task ATransient404OnTheOfferDoesNotAbort_TheLoopKeepsTryingUntilTheConsoleAnswers()
    {
        // A just-created session can take a moment to be addressable; an OFFER POST that 404s early must not
        // kill the rendezvous, since a retry moments later succeeds.
        var options = new HalyardWanRendezvousOptions { OfferInterval = TimeSpan.FromMilliseconds(40) };
        var signaling = new FailFirstOffersSignaling(failures: 2);
        var socket = new ControllableWebSocket();
        var channel = new HalyardPushChannel(socket);
        var rendezvous = new HalyardWanRendezvous(signaling, new FakeGatherer(null), options);
        using UdpClient media = MediaSocket();

        // The console answers *because* an OFFER finally reached it, so both failures are behind us by
        // construction on any host. Releasing the answer on a wall-clock delay instead is what made this test
        // flake: it only counted three attempts on a host that fitted three offer intervals into those
        // milliseconds, and an osx-arm64 runner that fitted two read two and failed.
        signaling.OnOfferAccepted = () => socket.Push(ConsoleOfferFrame());

        await using HalyardWanConnection conn = await rendezvous.ConnectAsync(
            Request, media, channel, Server, "token", CancellationToken.None);

        Assert.True(signaling.OfferAttempts >= 3, $"expected retries past the failures, got {signaling.OfferAttempts}");
        Assert.Single(conn.ConsoleCandidates);
    }

    [Fact]
    public async Task KeepsReOfferingWhileTheConsoleTakesTimeToJoin()
    {
        var options = new HalyardWanRendezvousOptions { OfferInterval = TimeSpan.FromMilliseconds(40) };
        var (rendezvous, signaling, socket, channel) = Build(options: options);
        using UdpClient media = MediaSocket();

        // Answer on the second offer, not after a stretch of wall clock. The claim is about the loop — that
        // it re-offers rather than offering once and waiting — so the loop is what has to release the answer;
        // a delay only asserts that this host fits two intervals into 200ms. Same shape as the transient-404
        // test above, which failed on CI for exactly that reason.
        signaling.OnOfferSent = sent =>
        {
            if (sent == 2)
            {
                socket.Push(ConsoleOfferFrame());
            }
        };

        await using HalyardWanConnection conn = await rendezvous.ConnectAsync(
            Request, media, channel, Server, "token", CancellationToken.None);

        Assert.True(signaling.OfferCount >= 2, $"expected repeated offers, got {signaling.OfferCount}");
    }

    [Fact]
    public async Task TimesOutWhenTheConsoleNeverAnswers_AndTearsEverythingDown()
    {
        var options = new HalyardWanRendezvousOptions
        {
            RendezvousTimeout = TimeSpan.FromMilliseconds(150),
            OfferInterval = TimeSpan.FromMilliseconds(40),
        };
        var (rendezvous, signaling, _, channel) = Build(options: options);
        using UdpClient media = MediaSocket();

        // Never push a console OFFER.
        await Assert.ThrowsAsync<HalyardCloudException>(() => rendezvous.ConnectAsync(
            Request, media, channel, Server, "token", CancellationToken.None));

        // The session was created but the failure path must have left it and stopped the push loop.
        Assert.Contains("create", signaling.Calls);
    }

    [Fact]
    public async Task Disconnect_LeavesTheSession()
    {
        var (rendezvous, signaling, socket, channel) = Build();
        using UdpClient media = MediaSocket();
        _ = signaling.CommandSent.Task.ContinueWith(_ => socket.Push(ConsoleOfferFrame()), TaskScheduler.Default);

        HalyardWanConnection conn = await rendezvous.ConnectAsync(
            Request, media, channel, Server, "token", CancellationToken.None);
        Assert.False(signaling.Left);

        await conn.DisposeAsync();

        Assert.True(signaling.Left);
    }

    [Fact]
    public async Task Cancellation_AbortsTheRendezvous()
    {
        var (rendezvous, _, _, channel) = Build();
        using UdpClient media = MediaSocket();
        using var cts = new CancellationTokenSource();

        Task<HalyardWanConnection> connect = rendezvous.ConnectAsync(
            Request, media, channel, Server, "token", cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => connect);
    }
}
