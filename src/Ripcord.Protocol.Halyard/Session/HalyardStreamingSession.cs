using System.Globalization;
using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Ripcord.Core.Discovery;
using Ripcord.Core.Net.Udp;
using Ripcord.Core.Reactive;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Discovery;
using Ripcord.Protocol.Halyard.Common.Control;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Input;
using Ripcord.Protocol.Halyard.Common.Streaming;
using Ripcord.Protocol.Halyard.Takion;
using Ripcord.Protocol.Halyard.Transport;

namespace Ripcord.Protocol.Halyard.Session;

/// <summary>
/// How the client reached the console, as the console is told in <c>RP-ConPath</c> on <c>/sess/ctrl</c>.
///
/// <para>
/// It is not decoration: the console runs a different bring-up for each. On <see cref="Local"/> it hands the
/// A/V leg straight to :9297 and expects Takion there immediately. On <see cref="Rendezvous"/> it instead
/// offers a second connection through the cloud, which the client must answer and prelude before Takion means
/// anything — and declaring <see cref="Local"/> while taking the rendezvous route makes the console wait for
/// a leg the client will never open on those terms.
/// </para>
///
/// <para>
/// Only 1 and 3 have been seen on the wire ([W], LAN captures and cap63/cap64 respectively). 2 is unclaimed
/// and deliberately unnamed rather than guessed at.
/// </para>
/// </summary>
public enum HalyardConnectionPath
{
    /// <summary>Reached directly on the LAN.</summary>
    Local = 1,

    /// <summary>Reached through the account rendezvous, whether the peer turned out to be on-LAN or not.</summary>
    Rendezvous = 3,
}

/// <summary>Where and how to reach a specific console for a direct session.</summary>
/// <param name="DeviceId">This client's device id (the hex-decoded Windows MachineGuid) for the RP-Did
/// field; empty until threaded from the platform layer.</param>
/// <param name="ConnectionPath">Which route got us here; see <see cref="HalyardConnectionPath"/>.</param>
public sealed record HalyardConnectionParameters(
    string ConsoleId,
    IPEndPoint ControlEndpoint,
    IPEndPoint StreamEndpoint,
    ReadOnlyMemory<byte> DeviceId = default,
    HalyardConnectionPath ConnectionPath = HalyardConnectionPath.Local);

/// <summary>
/// The direct-console session: runs the /sess handshake over the control channel, establishes the
/// crypto seam, then opens the stream socket and demuxes video/audio into the neutral observables
/// while sending controller input. With the passthrough crypto the handshake is well-formed but a
/// real console rejects it at the MAC check (expected until Stage 5); against replayed captures the
/// stream path runs fully.
/// </summary>
/// <summary>
/// A stream transport prepared by someone who knows how to get one: the socket to run Takion over, and the
/// endpoint to run it against.
/// </summary>
/// <param name="Socket">Owned by the session from here; it disposes it with everything else.</param>
public sealed record HalyardStreamTransport(UdpChannel Socket, IPEndPoint Endpoint);

/// <summary>
/// Produces the A/V transport when the LAN default will not do. The account route needs this: its stream leg
/// is a separately negotiated connection reached on a different port, opened with its own prelude.
/// </summary>
public delegate Task<HalyardStreamTransport> HalyardStreamTransportFactory(CancellationToken cancellationToken);

public sealed class HalyardStreamingSession : IStreamingSession
{
    private readonly HalyardConnectionParameters _parameters;
    private readonly IHalyardControlChannel _control;
    private readonly IHalyardSessionCrypto _crypto;
    private readonly IConsoleCredentialStore _credentials;

    private readonly Subject<EncodedVideoFrame> _video = new();
    private readonly Subject<EncodedAudioFrame> _audio = new();
    private readonly Subject<SessionStatistics> _stats = new();
    private readonly HalyardStreamDemuxer _demuxer;
    private readonly CancellationTokenSource _sessionCts = new();

    private UdpChannel? _streamSocket;
    private HalyardTakionStream? _takionStream;
    private HalyardSessionInputSink? _inputSink;

    // Input packets are handed to a single-writer/single-reader queue and sent by one drain loop, in order.
    // Previously each packet was fire-and-forget (`_ = SendAsync(...)`), so N concurrent sends raced: the
    // writer assigns sequence numbers synchronously but the datagrams could reach the wire out of order, and
    // any send failure became an unobserved task exception. Bounded + DropOldest because stale controller
    // input is worthless — under backpressure the newest state is the only one worth delivering.
    private Channel<byte[]>? _inputQueue;
    private Task? _inputSendLoop;
    private const int InputQueueCapacity = 256;
    private AdaptiveBandwidthController? _bandwidth;

    /// <summary>Decides when a CONNECTION_QUALITY report is worth sending (see its own docs for the cadence).</summary>
    private readonly ConnectionQualityReporter _qualityReporter = new();
    private HalyardPairingRecord? _pairing;
    private SessionConfig? _config;
    private Task? _ctrlKeepAlive;

    // Supplies the console login passcode when the console reports its user is locked. The bool is true on a
    // re-prompt after a rejected passcode, so the UI can say so. Null for headless callers (they simply cannot
    // sign a locked console in). Set at construction.
    private readonly Func<bool, CancellationToken, Task<string?>>? _loginPinProvider;

    // Set by the ctrl reader when the console pushes its sign-in prompt / session-ready frames, awaited by the
    // sign-in gate. TrySetResult because a message may arrive more than once (retries) and the first wins.
    private readonly TaskCompletionSource _loginPromptReceived =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _sessionReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _streamReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public HalyardStreamingSession(
        HalyardConnectionParameters parameters,
        IHalyardControlChannel control,
        IHalyardSessionCrypto crypto,
        IConsoleCredentialStore credentials,
        Func<bool, CancellationToken, Task<string?>>? loginPinProvider = null,
        TimeSpan? controlPlaneDeadline = null,
        HalyardStreamTransportFactory? streamTransportFactory = null)
    {
        _streamTransportFactory = streamTransportFactory;
        _controlPlaneDeadline = controlPlaneDeadline ?? DefaultControlPlaneDeadline;
        _parameters = parameters;
        _control = control;
        _crypto = crypto;
        _credentials = credentials;
        _loginPinProvider = loginPinProvider;
        _demuxer = new HalyardStreamDemuxer(crypto);
        _demuxer.VideoFrameReady += frame => _video.OnNext(frame);
        _demuxer.AudioFrameReady += frame => _audio.OnNext(frame);
    }

    public SessionState State { get; private set; } = SessionState.Connecting;
    public IObservable<EncodedVideoFrame> VideoFrames => _video;
    public IObservable<EncodedAudioFrame> AudioFrames => _audio;
    public IObservable<SessionStatistics> Statistics => _stats;

    /// <inheritdoc />
    /// <remarks>Seeded from the config in <c>ConnectAsync</c>; the app overrides it from the disconnect prompt
    /// just before teardown, so this — not the frozen config value — is what <c>DisposeAsync</c> honours.</remarks>
    public bool RestConsoleOnDisconnect { get; set; }

    /// <inheritdoc />
    public double? MillisecondsSinceConsoleActivity => _takionStream?.MillisecondsSinceConsoleActivity;

    /// <summary>
    /// Turn each congestion-window (received, lost) A/V unit sample into a <see cref="SessionStatistics"/> so
    /// consumers (the on-screen readout) can show the wire loss ratio. Only loss + the requested bitrate are
    /// known here; frame rate / decode time come from the decode pipeline on the app side.
    /// </summary>
    private void OnPacketStatsSampled(long received, long lost)
    {
        long total = received + lost;
        double lossRatio = total > 0 ? (double)lost / total : 0.0;
        double rttMs = _takionStream?.RoundTripTimeMs ?? 0;

        // Feed the adaptive controller. This is the loop that was missing entirely: the samples were computed
        // and reported to the UI, but nothing ever acted on them, so quality never responded to the link.
        // total is passed so the controller can tell a real loss rate from one lost unit in a tiny startup window.
        _bandwidth?.ReportNetworkSample(new NetworkSample(rttMs, lossRatio, JitterMs: 0, ObservedUnits: total));

        // …and now act on its decision. Deciding without telling the console was the remaining gap: the ladder
        // moved but nothing on the wire changed. CONNECTION_QUALITY also carries our measured RTT and loss, so
        // the console's own rate controller works from what we actually see rather than inferring it.
        if (_config?.ReportConnectionQuality == true && _bandwidth is not null && _takionStream is not null)
        {
            BitrateDecision decision = _bandwidth.RecommendBitrate();
            ConnectionQualityReport? report = _qualityReporter.Next(
                decision.BitrateKbps, rttMs, lossRatio * 100.0, DateTimeOffset.UtcNow);

            if (report is { } toSend)
            {
                _takionStream.ReportConnectionQuality(toSend);
            }
        }

        _stats.OnNext(new SessionStatistics(
            RoundTripTimeMs: rttMs,
            // The measured wire rate, not the rate we asked for. Echoing the request back looked like telemetry
            // but told the user nothing they had not already chosen.
            BitrateKbps: _takionStream?.MeasuredBitrateKbps ?? 0,
            Fps: 0,
            PacketLossRatio: lossRatio,
            DecodeTimeMs: 0,
            // What the bring-up measured and the launchSpec declared, so the overlay can show measurements rather
            // than leaving the reader to assume the old hardcoded 1454/0.
            DeclaredMtu: _confirmedMtu ?? LinkMetrics.MtuToDeclare(_measuredMtu),
            DeclaredRttMs: _measuredRttMs,
            MtuConfirmed: _confirmedMtu is not null,
            ReceiveQueueDepth: _takionStream?.AvQueueDepth ?? 0));
    }
    /// <summary>
    /// Ask the console for a fresh IDR. Safe to call before the stream is up (a no-op) and rate-limited inside
    /// the Takion stream, so callers can fire it on every detected corruption.
    /// </summary>
    public void RequestKeyFrame() => _takionStream?.RequestKeyFrame();

    public ISessionInputSink InputSink => _inputSink ?? throw new InvalidOperationException("Connect before using InputSink.");
    public IBandwidthController BandwidthController => _bandwidth ?? throw new InvalidOperationException("Connect before using BandwidthController.");

    /// <summary>
    /// Longest the CONTROL PLANE may take (pairing load through /sess/ctrl) before the handshake is reported as a
    /// failure.
    ///
    /// <para>
    /// Scoped to the control plane deliberately. The senkusha and stream phases already bound themselves (8s and
    /// 12s), so extending this deadline over them would mean a control plane that legitimately took 20s left the
    /// stream phase 5s — turning a slow-but-working link into a failure, i.e. inventing a new problem while fixing
    /// another. Only the /sess/init and /sess/ctrl response reads were unbounded, and only they are covered.
    /// </para>
    /// </summary>
    /// <summary>
    /// The default, which suits a directly-reached LAN console. The account route needs longer and says so:
    /// its console gates the control plane on a cloud rendezvous completing, and one was measured taking about
    /// twenty seconds to send its signaling ACCEPT -- long enough that a legitimately working link failed here.
    /// </summary>
    public static readonly TimeSpan DefaultControlPlaneDeadline = TimeSpan.FromSeconds(20);

    private readonly TimeSpan _controlPlaneDeadline;
    private readonly HalyardStreamTransportFactory? _streamTransportFactory;

    /// <summary>
    /// Which handshake step is in flight, so a timeout can name it. Set as the handshake advances and read only on
    /// failure; a torn read would at worst misname a step in a message.
    /// </summary>
    private volatile string _connectStep = "starting";

    public async Task<SessionHandshakeResult> ConnectAsync(SessionConfig config, CancellationToken cancellationToken)
    {
        _bandwidth = new AdaptiveBandwidthController(config);

        // A DEADLINE over the whole handshake. The senkusha and stream phases already bound themselves, but the
        // control plane did not: /sess/init and /sess/ctrl awaited a response with only a cancellation token, so a
        // console that accepts the TCP connection and then never answers left the app on "Connecting…" indefinitely
        // with nothing to report. Observed on a fresh machine, where it is the least diagnosable outcome possible.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_controlPlaneDeadline);
        CancellationToken token = deadline.Token;

        try
        {
            _connectStep = "loading pairing";
            _pairing = await LoadPairingAsync(token).ConfigureAwait(false);
            byte[]? registrationKey = _pairing?.RegistrationKey;
            _config = config;
            RestConsoleOnDisconnect = config.RestConsoleOnDisconnect;

            // The console family drives the family-specific wire details below (the SRC2/SRC3 arming probe, the
            // /sie/ps4|ps5/ paths, RP-Version, and the control-KDF variant). A legacy record with no platform
            // deserializes as PS5, which is what every pre-PS4 pairing is.
            HalyardConsolePlatform platform = _pairing?.Platform ?? HalyardConsolePlatform.Ps5;

            // Arm the console's control TCP listener before connecting: the console opens :9295 only after
            // hearing the UDP search probe (SRC3 for PS5, SRC2 for PS4); a cold TCP connect is refused with a
            // RST. (The same probe the registration path uses — wire-confirmed a single probe arms init + ctrl.)
            _connectStep = "probing the console's control listener";
            await HalyardControlSearch.ProbeAsync(
                _parameters.ControlEndpoint.Address.ToString(), ps5: platform == HalyardConsolePlatform.Ps5, token).ConfigureAwait(false);

            _connectStep = "opening the control connection";
            await _control.ConnectAsync(_parameters.ControlEndpoint, token).ConfigureAwait(false);

            // The ephemeral ECDH keypair is generated later, inside the Takion SESSION_REQUEST/REPLY exchange
            // (the negotiator picks the curve from the negotiated protocol version).

            _connectStep = "/sess/init";
            SessResponse initResponse = await SendInitAsync(registrationKey, token).ConfigureAwait(false);
            if (!initResponse.IsSuccess)
            {
                return Fail($"/sess/init rejected ({Describe(initResponse)}).");
            }

            // v1 control-plane key establishment: KDF over (RP-Nonce || companion). Establish the control
            // key when the pairing record supplies the companion. The version selector is the KDF-variant /
            // context-key discriminator: PS4 (older protocol) resolves to mode 0, PS5 to mode 1 — wire-derived
            // from each console's own /sess/ctrl RP-Auth. The codec selector is the value our captures resolve
            // to; pinning the full RP-KeyType -> selector mapping is a refinement tracked in the spec.
            byte[] nonce = DecodeBase64Header(initResponse, SessProtocol.HeaderNonce);
            if (nonce.Length == 16 && _pairing?.Companion is { Length: 16 } companion)
            {
                int versionSelector = platform == HalyardConsolePlatform.Ps4 ? 0 : 1;
                _crypto.EstablishControl(new HalyardControlKeyMaterial(
                    nonce, companion, CodecSelector: 2, VersionSelector: versionSelector));
            }

            // /sess/init was served with Connection: close, so the console closed that socket. Open a fresh
            // connection for /sess/ctrl (keep-alive), which becomes the persistent control channel. The single
            // SRC3 probe already armed the listener for this whole window, so no re-probe is needed.
            _connectStep = "reopening the control connection";
            await _control.ConnectAsync(_parameters.ControlEndpoint, token).ConfigureAwait(false);

            _connectStep = "/sess/ctrl";
            SessResponse ctrlResponse = await SendControlAsync(token).ConfigureAwait(false);
            if (!ctrlResponse.IsSuccess)
            {
                return Fail($"/sess/ctrl rejected ({Describe(ctrlResponse)}).");
            }

            // The /sess/ctrl HTTP response is immediately followed, on the SAME TCP connection, by a
            // persistent binary control channel. The console sends HEARTBEAT_REQ every few seconds and tears
            // the whole session down (RST) if the client stops answering — wire-confirmed: without this loop
            // the console drops us ~15-30s in, right after A/V starts. Run the keep-alive for the session.
            _ctrlKeepAlive = Task.Run(() => RunCtrlKeepAliveAsync(_sessionCts.Token));

            // Sign-in gate. A locked console pushes a login prompt on the channel just opened and then
            // silently drops every Takion INIT until the passcode is submitted — so this must complete before
            // senkusha/Takion, not race them. An unlocked console sends no prompt and this returns at once.
            _connectStep = "sign-in";
            SessionHandshakeResult? signIn = await EnsureSignedInAsync(cancellationToken).ConfigureAwait(false);
            if (signIn is not null)
            {
                return signIn;
            }

            // Senkusha bring-up on UDP 9297 — the console requires it between /sess/ctrl and the stream, or
            // it never answers the stream's SESSION exchange. Non-fatal (the vendor tolerates failures here).
            // Past the control plane: back to the caller's token so these phases keep their own budgets.
            //
            // Skipped entirely on the rendezvous route, where it is not merely useless but actively harmful:
            // the probe opens its own bare socket to :9297, and on that route :9297 answers nobody who has not
            // completed a prelude there. So it can only ever time out — and it spends those eight seconds
            // firing unanswerable Takion INITs at the very port the real A/V leg is about to negotiate, from a
            // source port the console was never told about.
            if (_parameters.ConnectionPath == HalyardConnectionPath.Local)
            {
                _connectStep = "senkusha bring-up";
                using var probeSocket = new UdpChannel();
                await RunSenkushaAsync(
                    probeSocket,
                    new IPEndPoint(_parameters.ControlEndpoint.Address, SenkushaPort),
                    cancellationToken).ConfigureAwait(false);
            }

            _connectStep = "stream key agreement";
            TakionSessionResult streaming = await StartStreamingAsync(cancellationToken).ConfigureAwait(false);
            if (!streaming.Success)
            {
                return Fail($"Stream key agreement did not complete (Takion/SESSION): {streaming.FailureReason}");
            }

            State = SessionState.Streaming;
            return new SessionHandshakeResult(true, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own deadline fired, not the caller's cancellation. Name the step: "Connecting…" forever with no
            // stated cause is the single least diagnosable failure this app can produce, and on a machine with no
            // debugger it is the only information available.
            return Fail($"Control setup timed out after {_controlPlaneDeadline.TotalSeconds:F0}s at: {_connectStep}. "
                        + "The console did not answer. Check that no other device is streaming from it, and that "
                        + "inbound UDP is allowed for this app.");
        }
        catch (Exception ex)
        {
            return Fail($"{_connectStep}: {ex.Message}");
        }
    }

    /// <summary>
    /// The status, plus the console's own <c>RP-Application-Reason</c> when it gives one.
    ///
    /// <para>
    /// Without it every refusal reads identically, and they are not identical: on the registration path the
    /// difference between "wrong transport" and "wrong key" was two distinct reason codes, and not surfacing
    /// them cost a session of chasing the wrong one.
    /// </para>
    /// </summary>
    private static string Describe(SessResponse response)
    {
        string? reason = response.Header("RP-Application-Reason");
        return reason is null
            ? $"HTTP {response.StatusCode}"
            : $"HTTP {response.StatusCode}, RP-Application-Reason {reason}";
    }

    /// <summary>
    /// The <c>Host</c> value, with the octets right-aligned in three columns the way every captured vendor
    /// request writes them (<c>Host: 172. 16.  0.104:9295</c>) -- a <c>%3d.%3d.%3d.%3d</c> format.
    ///
    /// <para>
    /// A LAN console accepts the unpadded form, so this is not required there. It is one of only two things
    /// still differing from the captured request on the account route, which refuses <c>/sess/init</c> with
    /// <c>403 / 80108b13</c> while the same pairing record is accepted over TCP minutes earlier -- so being
    /// byte-faithful here costs nothing and removes a variable.
    /// </para>
    /// </summary>
    private string HostHeader()
    {
        IPEndPoint endpoint = _parameters.ControlEndpoint;
        if (endpoint.Address.AddressFamily != AddressFamily.InterNetwork)
        {
            return endpoint.ToString();
        }

        byte[] octets = endpoint.Address.GetAddressBytes();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{octets[0],3}.{octets[1],3}.{octets[2],3}.{octets[3],3}:{endpoint.Port}");
    }

    private Task<SessResponse> SendInitAsync(byte[]? registrationKey, CancellationToken cancellationToken)
    {
        // v1 /sess/init presents the RP-Registkey as the HEX encoding of the stored registration-key bytes
        // (wire-confirmed: header value "3161326233633464" = hex of the 8 bytes "1a2b3c4d") and receives the
        // RP-Nonce for the control KDF. (The modern RP-Pubkey/RP-Hmac headers belong to the deferred v2 protocol.)
        // Match the vendor's /sess/init exactly (wire-confirmed, cap22): HTTP/1.1, and ONLY the registkey +
        // version headers — it does NOT send RP-SupportCmd/RP-Feature on the init request (those are in the
        // *response*). RP-Registkey is the hex of the raw registration-key bytes.
        HalyardConsolePlatform platform = _pairing?.Platform ?? HalyardConsolePlatform.Ps5;
        string registKey = registrationKey is null ? string.Empty : Convert.ToHexString(registrationKey).ToLowerInvariant();
        var request = new SessRequest(SessHttpMethod.Get, SessProtocol.PathFor(platform, "init"), "HTTP/1.1")
            .Header("Host", HostHeader())
            .Header("User-Agent", "remoteplay Windows")
            .Header("Connection", "close")

            // NB: Content-Length is emitted by SessRequest.Serialize, which always writes one. Adding it here
            // too sent the header TWICE -- a duplicate Content-Length is a request-smuggling shape that strict
            // parsers reject outright, and the console answered 403. Found by diffing our bytes off the wire
            // against the captured request rather than by reading the code, which is the lesson.
            .Header(SessProtocol.HeaderRegistKey, registKey)

            // "Rp-Version" on init, not "RP-Version" -- which is what the captured client sends here, while
            // sending "RP-Version" on /sess/ctrl. HTTP header names are case-insensitive and a LAN console
            // treats them so; this matches the capture exactly rather than relying on that.
            .Header("Rp-Version", SessProtocol.VersionFor(platform));

        return _control.SendRequestAsync(request, cancellationToken);
    }

    private Task<SessResponse> SendControlAsync(CancellationToken cancellationToken)
    {
        HalyardConsolePlatform platform = _pairing?.Platform ?? HalyardConsolePlatform.Ps5;
        var request = new SessRequest(SessHttpMethod.Get, SessProtocol.PathFor(platform, "ctrl"))
            .Header("Host", HostHeader())
            .Header("User-Agent", "remoteplay Windows")
            .Header("Connection", "keep-alive")
            // Fixed plaintext fields the console requires alongside the encrypted ones (wire-confirmed, cap22).
            .Header(SessProtocol.HeaderVersion, SessProtocol.VersionFor(platform))
            .Header(SessProtocol.HeaderControllerType, "0")
            .Header(SessProtocol.HeaderClientType, "11")
            .Header(
                SessProtocol.HeaderConPath,
                ((int)_parameters.ConnectionPath).ToString(CultureInfo.InvariantCulture))
            .Header(SessProtocol.HeaderPadProcNo, "2")
            .Header(SessProtocol.HeaderSupportCmd, "060000");

        // With the control key established, /sess/ctrl carries the encrypted RP-Auth/RP-Did/RP-OSType/
        // RP-StartBitrate/RP-StreamingType fields (spec §2.1). Without it (no pairing companion yet), the
        // request goes out unauthenticated and the console rejects - the boundary the pairing record removes.
        if (_crypto.IsControlEstablished && _pairing is not null)
        {
            Version os = Environment.OSVersion.Version;
            int bitrate = _config?.InitialBitrateKbps ?? 10_000;
            // Use the injected device id, or fall back to this machine's real MachineGuid.
            ReadOnlySpan<byte> deviceId = _parameters.DeviceId.IsEmpty
                ? HalyardDeviceIdentity.Current().Span
                : _parameters.DeviceId.Span;
            var fields = HalyardSessCtrlFields.Build(
                _crypto,
                _pairing.RegistrationKey,
                deviceId,
                os.Major, os.Minor,
                startBitrate: bitrate,
                streamingType: 0);

            foreach (var (name, value) in fields)
            {
                request.Header(name, value);
            }
        }

        return _control.SendRequestAsync(request, cancellationToken);
    }

    /// <summary>
    /// Drain the persistent /sess/ctrl binary channel for the life of the session and answer the console's
    /// HEARTBEAT_REQ with HEARTBEAT_REP. Missing these is what makes the console disconnect shortly after A/V
    /// begins. Other message types (session id, login, features) are functional refinements and ignored for
    /// now — the heartbeat reply is the keep-alive. Empty-payload messages need no control-plane crypto, so this stays
    /// simple. A closed connection or cancellation ends the loop quietly.
    /// </summary>
    /// <summary>How long to watch for a login prompt before assuming the console is unlocked. In cap50 the
    /// prompt arrived ~60 ms after /sess/ctrl; a second is generous and bounds the added latency on the common
    /// (unlocked) path.</summary>
    private static readonly TimeSpan LoginPromptWindow = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait for the console's session-ready after a passcode is submitted. The console
    /// accepts or rejects fast — in cap50 the session-ready arrived ~2.3 s after submit — so a few seconds
    /// with no session-ready means the passcode was wrong (cap51: the console just waits for the next
    /// attempt). Short so a wrong passcode re-prompts quickly rather than hanging.</summary>
    private static readonly TimeSpan SignInAttemptTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Passcode attempts before giving up. The console tolerated at least six on one connection
    /// (cap51); this bound is our own, to end the loop if the user keeps mistyping rather than cancelling.</summary>
    private const int MaxSignInAttempts = 5;

    /// <summary>
    /// How long the A/V leg waits for the console's session-ready frame before opening Takion anyway.
    ///
    /// <para>
    /// Measured gaps between the A/V prelude finishing and that frame arriving were 2.3–2.9 s across three
    /// captured rendezvous sessions, so this is roughly triple the longest. It deliberately does not fail the
    /// session on expiry: session-ready gates the console's stream service on the rendezvous route, but a LAN
    /// console reached the old way streams without ever sending one, and turning a missing frame into a hard
    /// failure would trade a diagnosable timeout for an undiagnosable refusal.
    /// </para>
    /// </summary>
    private static readonly TimeSpan SessionReadyWindow = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long the A/V leg waits for the console's stream-ready frame after the probe. Measured gaps were
    /// 0.5–1.5 s across the captured rendezvous sessions; this is generous, and expiring is not a failure —
    /// see the call site.
    /// </summary>
    private static readonly TimeSpan StreamReadyWindow = TimeSpan.FromSeconds(10);

    /// <summary>
    /// If the console's user is locked, complete the login before the stream is attempted; a locked console
    /// silently drops every Takion INIT until the passcode is accepted (cap50). Returns null to proceed, or a
    /// failure result to abort. An unlocked console sends no prompt and this returns null after the short
    /// watch window.
    ///
    /// <para>Success is the console's session-ready frame, and only that: a wrong passcode draws an immediate
    /// login-result frame whose byte is opaque and different every time (cap51), so it cannot be read as
    /// pass/fail — the reliable signal is that session-ready follows on success and does not on failure. On a
    /// rejection the console waits for another attempt on the same connection, so we re-prompt rather than
    /// fail the session.</para>
    /// </summary>
    private async Task<SessionHandshakeResult?> EnsureSignedInAsync(CancellationToken cancellationToken)
    {
        // Wait briefly for the console to say "this user is locked". No prompt → unlocked → nothing to do.
        if (await CompletesWithin(_loginPromptReceived.Task, LoginPromptWindow, cancellationToken).ConfigureAwait(false) is false)
        {
            return null;
        }

        if (_loginPinProvider is null)
        {
            return Fail("This console requires a login passcode, but no passcode entry is available here.");
        }
        if (!_crypto.IsControlEstablished)
        {
            return Fail("This console requires a login passcode, but the control-plane crypto is not established.");
        }

        // The field counter is a single per-connection value; the passcode continues it past the five
        // /sess/ctrl fields (so the first attempt is 5), and each retry MUST advance it — a fresh IV per
        // submit, never reused (cap51's attempts each had distinct ciphertext).
        ulong counter = HalyardSessCtrlFields.CounterLoginPin;

        for (int attempt = 1; attempt <= MaxSignInAttempts; attempt++)
        {
            string? pin = await _loginPinProvider(attempt > 1, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(pin))
            {
                return Fail("Sign-in cancelled: no login passcode entered.");
            }

            byte[] plaintext = HalyardSessCtrlFields.BuildLoginPinPlaintext(pin);
            byte[] ciphertext = _crypto.EncryptControlField(counter++, plaintext);
            await _control.SendCtrlMessageAsync(
                new HalyardCtrlMessage(HalyardCtrlMessage.TypeLoginSubmit, ciphertext), cancellationToken).ConfigureAwait(false);

            if (await CompletesWithin(_sessionReady.Task, SignInAttemptTimeout, cancellationToken).ConfigureAwait(false))
            {
                return null; // accepted
            }
            // No session-ready: the passcode was rejected. Loop and re-prompt (attempt > 1 tells the UI to say so).
        }

        return Fail($"Sign-in failed: the login passcode was rejected {MaxSignInAttempts} times.");
    }

    /// <summary>True if <paramref name="task"/> completes within <paramref name="timeout"/>; false on timeout.
    /// Cancellation propagates. Does not fault on the awaited task's own exceptions — callers only care that it
    /// signalled.</summary>
    private static async Task<bool> CompletesWithin(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task delay = Task.Delay(timeout, timeoutCts.Token);
        Task winner = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (winner == delay)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        timeoutCts.Cancel(); // stop the delay timer
        return true;
    }

    private async Task RunCtrlKeepAliveAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                HalyardCtrlMessage? message = await _control.ReadCtrlMessageAsync(cancellationToken).ConfigureAwait(false);
                if (message is null)
                {
                    return; // control connection closed
                }

                TraceCtrlFrame(message.Value);

                switch (message.Value.Type)
                {
                    case HalyardCtrlMessage.TypeHeartbeatReq:
                        await _control.SendCtrlMessageAsync(
                            new HalyardCtrlMessage(HalyardCtrlMessage.TypeHeartbeatRep), cancellationToken).ConfigureAwait(false);
                        break;

                    case HalyardCtrlMessage.TypeLoginPrompt:
                        // Console: this user is locked, send the passcode. The sign-in gate is waiting on this.
                        _loginPromptReceived.TrySetResult();
                        break;

                    case HalyardCtrlMessage.TypeSessionId:
                        // Console: session ready. After a login this is the "you may open the stream" signal.
                        _sessionReady.TrySetResult();
                        break;

                    case HalyardCtrlMessage.TypeStreamReady:
                        // Console: the stream service is up. The rendezvous route's A/V leg waits on this.
                        _streamReady.TrySetResult();
                        break;

                }
            }
        }
        catch (OperationCanceledException)
        {
            // session shutting down
        }
        catch (Exception)
        {
            // control channel faulted (e.g. socket closed under us) — the session teardown will observe it
        }
    }

    /// <summary>
    /// Drain the input queue, awaiting each send so packets reach the wire in the order their sequence numbers
    /// were assigned. A send failure is logged-and-swallowed per packet: input is an unreliable channel by
    /// design (the writer re-sends recent history events), so one lost datagram must not end the session.
    /// </summary>
    /// <summary>
    /// The counter the console's next payload-carrying control frame is encrypted at.
    ///
    /// <para>
    /// The control-field cipher's counter is per-connection and <b>shared across the whole direction</b>: the
    /// console's <c>/sess/ctrl</c> response spends 0, and every payload-carrying frame after it takes the next
    /// one. Heartbeats carry no payload and spend nothing. Confirmed against the console's own bytes with a
    /// known-plaintext oracle — the session-id frame decrypts at counter 2 to a length-prefixed
    /// <c>"InvalidSessionId"</c>, and at no other counter to anything at all.
    /// </para>
    /// </summary>
    private ulong _consoleFieldCounter = 1;

    /// <summary>
    /// Decrypt and dump a control frame, for the frames nobody has decoded yet (<c>0x0016</c>, <c>0x0017</c>,
    /// <c>0x0003</c>, and the session id's own payload). Diagnostic only, and off unless
    /// <c>RIPCORD_TRACE_CTRL</c> is set.
    /// </summary>
    private void TraceCtrlFrame(HalyardCtrlMessage message)
    {
        if (message.Payload.Length == 0 || !_crypto.IsControlEstablished)
        {
            return;
        }

        ulong counter = _consoleFieldCounter++;
        if (Environment.GetEnvironmentVariable("RIPCORD_TRACE_CTRL") is null)
        {
            return;
        }

        try
        {
            byte[] plain = _crypto.DecryptControlField(counter, message.Payload.Span);
            var text = new string([.. plain.Select(b => b is >= 0x20 and < 0x7f ? (char)b : '.')]);
            Console.Error.WriteLine(
                $"[ctrl] type=0x{message.Type:X4} len={message.Payload.Length} counter={counter} "
                + $"plain={Convert.ToHexString(plain)}  {text}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ctrl] type=0x{message.Type:X4} counter={counter}: {ex.GetType().Name}");
        }
    }

    private async Task RunInputSendLoopAsync(CancellationToken cancellationToken)
    {
        ChannelReader<byte[]>? reader = _inputQueue?.Reader;
        if (reader is null)
        {
            return;
        }

        try
        {
            await foreach (byte[] packet in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    HalyardTakionStream? stream = _takionStream;
                    if (stream is not null)
                    {
                        await stream.SendAsync(packet, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception)
                {
                    // Single-packet send failure; keep draining.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // session shutting down
        }
    }

    private async Task<HalyardPairingRecord?> LoadPairingAsync(CancellationToken cancellationToken)
    {
        byte[]? blob = await _credentials.LoadAsync(_parameters.ConsoleId, cancellationToken).ConfigureAwait(false);
        if (blob is null)
        {
            return null;
        }

        // The store persists a serialized pairing record; a legacy blob (a bare registration key with no
        // companion) is tolerated so older stores still drive /sess/init.
        return HalyardPairingRecord.TryDeserialize(blob, out HalyardPairingRecord? record)
            ? record
            : new HalyardPairingRecord(blob, Companion: [], KeyType: 0);
    }

    /// <summary>The senkusha bring-up port (UDP), one above the A/V stream port (wire-confirmed 9297).</summary>
    private const int SenkushaPort = 9297;

    // Takion handshake retransmit, matched to the vendor on a lossy link (cap55, 2026-08-03): it retransmits
    // each handshake chunk — notably COOKIE_ECHO — on a fixed ~300 ms timer (NOT exponential backoff) and
    // persists ~100 tries / ~30 s before giving up silently. Our old 2 s × 3 (~6 s) retransmitted ~7x slower
    // and gave up ~5x sooner, so under packet loss it failed exactly where the vendor recovered.
    private static readonly TimeSpan HandshakeRetransmitInterval = TimeSpan.FromMilliseconds(300);

    // The stream connection is the one whose handshake failure ends the attempt, so it gets the vendor's full
    // patience. The bring-up cancellation below (StreamBringUpTimeout) is the real outer bound.
    private const int StreamHandshakeAttempts = 100; // × ~300 ms ≈ 30 s per phase

    // Senkusha is a non-fatal MTU/bandwidth probe; retransmit just as fast, but keep its budget inside the
    // ~8 s box below so a marginal probe never stalls the stream that follows it.
    private const int SenkushaHandshakeAttempts = 20; // × ~300 ms ≈ 6 s

    // Whole stream bring-up (handshake + SESSION exchange). Widened from 12 s so the ~30 s handshake budget
    // above can actually run before this cuts it — a lossy link now gets the vendor's ~30 s, not ~6 s.
    private static readonly TimeSpan StreamBringUpTimeout = TimeSpan.FromSeconds(35);

    // What the bring-up measured, for the launchSpec. Null means "not measured", which is why the launchSpec falls
    // back to the vendor defaults rather than declaring 0.
    private double? _measuredRttMs;
    private int? _measuredMtu;

    // Set only when the senkusha probe verified the path in both directions; null means "declare the estimate".
    private int? _confirmedMtu;

    /// <summary>
    /// Run the senkusha bring-up on a Takion association to :9297 before the stream. Best-effort: a
    /// timeout or error is swallowed so the stream attempt still proceeds (matching the vendor).
    ///
    /// <para>
    /// The caller supplies the transport because the two routes reach :9297 differently, and the probe has to
    /// go where the stream will. A LAN console is probed on a socket of its own; on the rendezvous route the
    /// only socket :9297 will answer is the negotiated A/V one, after its prelude — the captured client runs
    /// the probe as a first Takion association on exactly that socket, tears it down, and opens a second for
    /// the stream. Probing a fresh socket there reaches nobody, which is why this took a parameter.
    /// </para>
    /// </summary>
    private async Task RunSenkushaAsync(
        UdpChannel socket, IPEndPoint endpoint, CancellationToken cancellationToken)
    {
        await using var senkusha = new HalyardSenkusha(socket, endpoint);
        using var senkushaCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        senkushaCts.CancelAfter(TimeSpan.FromSeconds(8));
        // Measured on the way past: the interface that routes to the console bounds the datagram size, and this
        // costs no traffic.
        _measuredMtu = LinkMetrics.InterfaceMtuTowards(endpoint.Address);

        try
        {
            // The candidate handed to the probe is the interface-derived estimate; senkusha either confirms it on the
            // real path or declines to, and only a CONFIRMED value replaces it.
            int candidateMtu = LinkMetrics.MtuToDeclare(_measuredMtu);

            SenkushaResult result = await senkusha
                .RunAsync(HandshakeRetransmitInterval, SenkushaHandshakeAttempts, candidateMtu, senkushaCts.Token)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                _measuredRttMs = result.RoundTripTimeMs;

                // Measured beats inferred: the interface MTU bounds the first hop, whereas the probe exercised the
                // whole path in both directions. An unconfirmed probe leaves the estimate alone rather than
                // downgrading a link that is probably fine.
                if (result.ConfirmedMtu is int confirmed)
                {
                    _confirmedMtu = confirmed;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // senkusha timed out — non-fatal, proceed to the stream
        }
        catch (Exception)
        {
            // non-fatal — the stream attempt continues either way
        }
    }

    private async Task<TakionSessionResult> StartStreamingAsync(CancellationToken cancellationToken)
    {
        // The stream rides Takion over a dedicated UDP socket to the negotiated stream port. The demuxer
        // (already wired to the video/audio subjects) authenticates + decrypts A/V through the crypto seam.
        // Give the socket a large receive buffer: the A/V stream is bursty and the default (~64 KB) drops
        // datagrams on any brief receive-loop stall (→ slice corruption + choppy audio), especially at higher
        // bitrates/resolutions.
        // On the account route the A/V leg is a second negotiated connection, not the LAN stream port: the
        // console offers it separately, it is reached on 9297, and it opens with the same 88-byte prelude the
        // control association uses before any Takion byte flows. A caller that knows how to do that supplies
        // the prepared socket and endpoint; everyone else gets a fresh socket to the negotiated stream port.
        IPEndPoint streamEndpoint;
        if (_streamTransportFactory is not null)
        {
            HalyardStreamTransport prepared =
                await _streamTransportFactory(cancellationToken).ConfigureAwait(false);
            _streamSocket = prepared.Socket;
            streamEndpoint = prepared.Endpoint;

            // …and then wait to be invited. On this route the console does not serve Takion on the A/V leg the
            // moment the prelude finishes: in every captured rendezvous session it sits quiet for two to three
            // seconds after the prelude, sends its session-ready frame on the control plane, and only then does
            // the captured client send a single Takion INIT — which is answered immediately.
            //
            // We were sending that INIT six milliseconds after the prelude and then retransmitting it a hundred
            // times over thirty seconds into a service that was not listening yet. Waiting costs nothing when
            // the frame has already arrived (the gate is a latch), and a console that never sends it is a
            // console that was never going to answer.
            _connectStep = "waiting for the console's session-ready";
            await CompletesWithin(_sessionReady.Task, SessionReadyWindow, cancellationToken).ConfigureAwait(false);

            // …and only now can the probe run, because only now does a socket exist that :9297 will answer.
            // Skipping it entirely got a SESSION_REPLY carrying no public key at all: the console answers the
            // Takion handshake either way, and refuses the session that follows.
            _connectStep = "senkusha bring-up";
            await RunSenkushaAsync(_streamSocket, streamEndpoint, cancellationToken).ConfigureAwait(false);

            // The captured client does not open the stream association until the console says the stream
            // service is up, which it does a second or two after the probe. Not fatal on expiry: this is a
            // wait, and a console that never sends it is better diagnosed by the SESSION_REPLY that follows
            // than by a timeout here that hides it.
            _connectStep = "waiting for the console's stream-ready";
            if (!await CompletesWithin(_streamReady.Task, StreamReadyWindow, cancellationToken).ConfigureAwait(false))
            {
                _connectStep = "stream-ready never arrived; opening the stream association anyway";
            }
        }
        else
        {
            _streamSocket = new UdpChannel(receiveBufferBytes: 4 * 1024 * 1024);
            streamEndpoint = _parameters.StreamEndpoint;
        }

        _takionStream = new HalyardTakionStream(_streamSocket, streamEndpoint, _crypto, _demuxer);
        _takionStream.PacketStatsSampled += OnPacketStatsSampled;

        // Controller input goes up the same socket, sealed by the crypto seam. Enqueue rather than send
        // directly: the drain loop below preserves the order the writer stamped sequence numbers in.
        var writer = new HalyardInputPacketWriter(_crypto);
        _inputQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(InputQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });
        _inputSendLoop = Task.Run(() => RunInputSendLoopAsync(_sessionCts.Token));
        _inputSink = new HalyardSessionInputSink(writer, packet => _inputQueue.Writer.TryWrite(packet));

        var request = BuildSessionRequest();

        // Bound the whole stream bring-up so a stalled SESSION exchange fails cleanly instead of hanging
        // forever (the negotiator otherwise waits on the reply with no deadline).
        using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        streamCts.CancelAfter(StreamBringUpTimeout);
        try
        {
            return await _takionStream
                .StartAsync(request, HandshakeRetransmitInterval, StreamHandshakeAttempts, streamCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TakionSessionResult.Fail(
                $"stream bring-up did not complete within {StreamBringUpTimeout.TotalSeconds:F0}s (no COOKIE_ACK/SESSION_REPLY under loss)");
        }
        catch (TimeoutException)
        {
            return TakionSessionResult.Fail("Takion handshake: no INIT_ACK from the console (stream service not listening — is a user logged in?)");
        }
    }

    /// <summary>
    /// Assemble the SESSION_REQUEST inputs. From a wire-parsed real SESSION_REQUEST: <c>sessionKey</c> is the
    /// literal "InvalidSessionId", <c>encryptedKey</c> is unused (left empty by the negotiator), and
    /// <c>clientVersion</c> is 17 (0x11). The launchSpec is built from the spec §4.3 fields (the template
    /// corroborated against our own client's memory + wire keystream) carrying a fresh handshakeKey,
    /// out1-encrypted when the control key is available.
    /// </summary>
    private TakionSessionRequest BuildSessionRequest()
    {
        byte[] handshakeKey = RandomNumberGenerator.GetBytes(16);
        string launchSpecPlain = BuildLaunchSpecJson(handshakeKey);

        byte[] launchBytes = System.Text.Encoding.UTF8.GetBytes(launchSpecPlain);
        // out1-encrypt the launchSpec when control is established; otherwise send it as-is (stub/passthrough).
        //
        // COUNTER 0 IS NOW WIRE-CONFIRMED, and was not before. It had only ever been exercised by a synthetic
        // vector generator, which proves two implementations agree and nothing about what the console wants —
        // and it is a suspicious value, because RP-Auth already uses counter 0 with the CFB field cipher, so
        // the OFB keystream's first block is identical to that field's. On 2026-08-12 ports/ripcord-3ds sent a
        // launchSpec encrypted exactly this way to a real PS5 and got a SESSION_REPLY whose ecdhSignature
        // verified — which is only possible if the console decrypted this document and recovered the
        // handshakeKey inside it. Do not "fix" the counter.
        byte[] launchWire = _crypto.IsControlEstablished ? _crypto.CryptStreaminfo(0, launchBytes) : launchBytes;

        return new TakionSessionRequest(
            ClientVersion: 17,
            SessionKey: "InvalidSessionId",
            LaunchSpecJson: Convert.ToBase64String(launchWire),
            HandshakeKey: handshakeKey);
    }

    /// <summary>
    /// Build the launchSpec JSON. This mirrors the reference client's exact template (the console parses the
    /// decrypted JSON and rejects an incomplete one — silently, with no SESSION_REPLY), so the field set and
    /// order are reproduced verbatim; only width/height/fps/bitrate and the handshakeKey are parameterised.
    /// The bandwidth/MTU/RTT values fall back to defaults until the full senkusha probe supplies measured ones.
    /// </summary>
    private string BuildLaunchSpecJson(byte[] handshakeKey) => BuildLaunchSpecJson(
        handshakeKey,
        _config?.Width ?? 1280,
        _config?.Height ?? 720,
        _config?.TargetFps ?? 60,
        _config?.InitialBitrateKbps ?? 10_000,
        _config?.CodecPreference ?? Ripcord.Core.Sessions.VideoCodec.H264,
        _config?.RequestedDynamicRange ?? Ripcord.Core.Sessions.DynamicRange.Sdr,
        // Resolved here, not inside the builder: a confirmed MTU is already in declared form, and passing it
        // through MtuToDeclare again would subtract the overhead a SECOND time (1454 -> 1408).
        _confirmedMtu ?? LinkMetrics.MtuToDeclare(_measuredMtu),
        _measuredRttMs);

    /// <inheritdoc cref="BuildLaunchSpecJson(byte[])"/>
    internal static string BuildLaunchSpecJson(
        byte[] handshakeKey,
        int w,
        int h,
        int fps,
        int bitrate,
        Ripcord.Core.Sessions.VideoCodec codec,
        Ripcord.Core.Sessions.DynamicRange dynamicRange = Ripcord.Core.Sessions.DynamicRange.Sdr,
        int? declaredMtu = null,
        double? measuredRttMs = null)
    {
        // Measured where possible, vendor defaults otherwise. Both were previously hardcoded — mtu 1454 and
        // rtt 0 — which declared a measurement that had never been taken. The console reads these, and rtt 0 in
        // particular told it the link was instantaneous.
        //
        // declaredMtu arrives ALREADY in declared form (senkusha-confirmed, or the interface estimate converted by
        // the caller). Converting again here would subtract the IP/UDP allowance twice.
        int mtu = declaredMtu is int value && value > 0 ? value : LinkMetrics.VendorMtu;
        int rtt = (int)Math.Round(Math.Clamp(measuredRttMs ?? 0, 0, 1000));
        string handshakeKeyB64 = Convert.ToBase64String(handshakeKey);

        return "{"
            + "\"sessionId\":\"sessionId4321\","
            + "\"streamResolutions\":[" + BuildStreamResolutions(w, h, fps) + "],"
            + "\"network\":{\"bwKbpsSent\":" + bitrate + ",\"bwLoss\":0.001000,\"mtu\":" + mtu + ",\"rtt\":" + rtt + ",\"ports\":[53,2053]},"
            + "\"slotId\":1,"
            + "\"appSpecification\":{\"minFps\":" + fps + ",\"minBandwidth\":0,\"extTitleId\":\"ps3\",\"version\":1,\"timeLimit\":1,\"startTimeout\":100,\"afkTimeout\":100,\"afkTimeoutDisconnect\":100},"
            + "\"konan\":{\"ps3AccessToken\":\"accessToken\",\"ps3RefreshToken\":\"refreshToken\"},"
            + "\"requestGameSpecification\":{\"model\":\"bravia_tv\",\"platform\":\"android\",\"audioChannels\":\"5.1\",\"language\":\"sp\",\"acceptButton\":\"X\",\"connectedControllers\":[\"xinput\",\"ds3\",\"ds4\"],\"yuvCoefficient\":\"bt601\",\"videoEncoderProfile\":\"hw4.1\",\"audioEncoderProfile\":\"audio1\"},"
            + "\"userProfile\":{\"onlineId\":\"psnId\",\"npId\":\"npId\",\"region\":\"US\",\"languagesUsed\":[\"en\",\"jp\"]},"
            + "\"videoCodec\":\"" + VideoCodecName(codec) + "\","
            + "\"dynamicRange\":\"" + DynamicRangeName(dynamicRange) + "\","
            + "\"handshakeKey\":\"" + handshakeKeyB64 + "\","
            + AudioChannelsJson
            + "}";
    }

    /// <summary>
    /// The <c>audioChannels</c> declaration, reproduced verbatim from a captured vendor launchSpec.
    ///
    /// <para>
    /// It is a fixed template rather than derived from our own audio settings, deliberately: every value here
    /// already matches what the decode path handles (48 kHz, stereo, 16-bit — <c>sampleSize</c> is bytes per
    /// sample — and 480 samples per frame, so <c>maxFrameDataSize</c> 1920 = 480 x 2 x 2), and where the template
    /// says something surprising the safe move is to match the vendor rather than to reason about it.
    /// <c>isSigned: false</c> is the notable example: Opus decodes to signed PCM, so the field either means
    /// something other than the obvious or is vestigial. Either way, deviating from a wire-confirmed template is
    /// the risk, not matching it.
    /// </para>
    ///
    /// <para>
    /// This was the last vendor key we omitted. It was added separately from the video-side launchSpec fixes
    /// because audio already worked on the console's defaults, so a regression here had to be attributable to this
    /// change alone.
    /// </para>
    /// </summary>
    internal const string AudioChannelsJson =
        "\"audioChannels\":{\"name\":\"default\",\"encoderType\":\"opus\",\"audioChannelSettings\":["
        + "{\"audioChannelType\":0,\"isSigned\":false,\"sampleRate\":48000,\"sampleSize\":2,\"channels\":2,"
        + "\"maxFrameDataSize\":1920,\"samplesPerFrame\":480,\"bitrate\":64,\"isRawPcm\":false,\"fecMode\":2}]}";

    /// <summary>
    /// The launchSpec spelling of a dynamic range. Only <c>"SDR"</c> has been seen on the wire — from a captured
    /// vendor launchSpec — so <c>"HDR"</c> is an inference from the field existing with a value at all. If the
    /// console declines a launchSpec containing it, the token is the first thing to vary (<c>"HDR10"</c> and
    /// <c>"PQ"</c> being the plausible alternatives) before concluding HDR is unsupported.
    /// </summary>
    internal static string DynamicRangeName(Ripcord.Core.Sessions.DynamicRange range) => range switch
    {
        Ripcord.Core.Sessions.DynamicRange.Hdr => "HDR",
        _ => "SDR",
    };

    /// <summary>
    /// The launchSpec spelling of a codec. Plumbed from <see cref="SessionConfig.CodecPreference"/> rather than
    /// hardcoded so requesting HEVC is a configuration change; the decoder side must be able to honour whatever
    /// this asks for, so do not offer "hevc" until an HEVC MFT is actually selected.
    /// </summary>
    internal static string VideoCodecName(Ripcord.Core.Sessions.VideoCodec codec) => codec switch
    {
        Ripcord.Core.Sessions.VideoCodec.Hevc => "hevc",
        _ => "avc",
    };

    /// <summary>
    /// The resolution rungs a real client offers. Taken from a captured vendor launchSpec, which advertises all
    /// four — 640x360, 960x540, 1280x720, 1920x1080, every one at score 1..4 ascending — rather than a single
    /// pinned entry.
    /// </summary>
    internal static readonly (int Width, int Height)[] StandardLadder =
    [
        (640, 360),
        (960, 540),
        (1280, 720),
        (1920, 1080),
    ];

    /// <summary>
    /// Build the <c>streamResolutions</c> array: every standard rung up to and including the requested one, with
    /// ascending <c>score</c> so the requested resolution is the most preferred.
    ///
    /// <para>
    /// We used to send exactly one entry at score 10. The vendor client sends the whole ladder, and the reason
    /// matters: with a single entry the console has nothing to fall back to if it cannot or will not serve that
    /// resolution, and the field is our only means of expressing an ordered preference. Scores ascend and the
    /// requested resolution always holds the highest, which is the vendor's own arrangement (1080p at score 4) —
    /// so offering the lower rungs adds fallbacks without inviting a downgrade.
    /// </para>
    /// </summary>
    internal static string BuildStreamResolutions(int width, int height, int fps)
    {
        var rungs = new List<(int Width, int Height)>();
        foreach ((int rw, int rh) in StandardLadder)
        {
            // Strictly below the request; the request itself is appended last so it scores highest even when it
            // is not one of the standard rungs (a custom size still gets its fallbacks).
            if (rh < height)
            {
                rungs.Add((rw, rh));
            }
        }

        rungs.Add((width, height));

        var sb = new StringBuilder();
        for (int i = 0; i < rungs.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"resolution\":{\"width\":").Append(rungs[i].Width)
              .Append(",\"height\":").Append(rungs[i].Height)
              .Append("},\"maxFps\":").Append(fps)
              .Append(",\"score\":").Append(i + 1).Append('}');
        }

        return sb.ToString();
    }

    private SessionHandshakeResult Fail(string reason)
    {
        State = SessionState.Closed;
        return new SessionHandshakeResult(false, reason);
    }

    private static byte[] DecodeBase64Header(SessResponse response, string name)
    {
        string? value = response.Header(name);
        if (string.IsNullOrEmpty(value))
        {
            return [];
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        State = SessionState.Closed;

        // A clean goodbye before tearing anything down, sent while the connections are still up and on its own
        // short budget. Best-effort throughout: a console that never sees any of this is no worse off than
        // before these existed.
        using (var goodbyeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
        {
            // Rest-on-disconnect, if requested. Isolated in cap52 by diffing a rest-off vs a rest-on
            // disconnect: the ONLY difference was an empty 0x0050 frame on the binary control channel — the
            // Takion DISCONNECT below is byte-identical either way and does NOT carry the rest bit. So this
            // frame is what actually rests the console; without it the console stays awake.
            if (RestConsoleOnDisconnect)
            {
                try
                {
                    await _control.SendCtrlMessageAsync(
                        new HalyardCtrlMessage(HalyardCtrlMessage.TypeRestMode), goodbyeCts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // best-effort; teardown proceeds whether or not the console rests
                }
            }

            // The graceful Takion DISCONNECT the vendor sends at session end (cap48/cap52). Both rest and
            // no-rest disconnects send it, so it is the polite close, not the rest trigger. We previously just
            // dropped the socket.
            if (_takionStream is not null)
            {
                try
                {
                    await _takionStream.SendDisconnectAsync(string.Empty, goodbyeCts.Token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // never let the goodbye hold up teardown
                }
            }
        }

        await _sessionCts.CancelAsync().ConfigureAwait(false);

        if (_ctrlKeepAlive is not null)
        {
            try { await _ctrlKeepAlive.ConfigureAwait(false); } catch { /* ignore shutdown races */ }
        }

        _inputQueue?.Writer.TryComplete();
        if (_inputSendLoop is not null)
        {
            try { await _inputSendLoop.ConfigureAwait(false); } catch { /* ignore shutdown races */ }
        }

        if (_takionStream is not null)
        {
            _takionStream.PacketStatsSampled -= OnPacketStatsSampled;
            await _takionStream.DisposeAsync().ConfigureAwait(false);
        }

        _streamSocket?.Dispose();

        // The crypto seam is not itself IDisposable — keeping the interface free of a lifetime concern most
        // implementations don't have — but the v1 implementation holds an ephemeral ECDH key and a cached AES
        // block cipher per direction, so release them when the concrete type does have them.
        (_crypto as IDisposable)?.Dispose();

        await _control.DisposeAsync().ConfigureAwait(false);
        _video.OnCompleted();
        _audio.OnCompleted();
        _stats.OnCompleted();
        _sessionCts.Dispose();
    }
}
