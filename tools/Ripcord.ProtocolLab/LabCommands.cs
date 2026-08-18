using System.Buffers.Binary;
using System.Net;
using Ripcord.Core.Accounts;
using Ripcord.Core.Discovery;
using Ripcord.Core.Platform;
using Ripcord.Core.Input;
using Ripcord.Core.Sessions;
using Ripcord.Cloud.Halyard;
using Ripcord.Cloud.Halyard.Rendezvous;
using Ripcord.Core.Net.WebSockets;
using Ripcord.Presentation.Halyard.Pairing;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Discovery;
using Ripcord.Protocol.Halyard.Common.Input;
using Ripcord.Protocol.Halyard.Common.Streaming;
using Ripcord.Protocol.Halyard.Discovery;
using Ripcord.Protocol.Halyard.Session;
using Ripcord.Protocol.Halyard.Transport;

namespace Ripcord.ProtocolLab;

internal static class LabCommands
{
    // ---- Stage 5: registration (PIN pairing) ----

    /// <summary>
    /// register &lt;consoleIp&gt; &lt;passcode&gt; &lt;accountId&gt; [clientIdHex(32B)] [ps4|ps5]
    /// Drives a live first-time pairing: derives the transport key from a fresh context + the on-screen
    /// passcode, POSTs the registration request, and decrypts the response into the pairing record
    /// (registkey + companion). Recovery of those fields is robust to the still-open send-side material
    /// question (they ride in IV-free CFB blocks); a console rejection here is itself the answer to whether
    /// the console requires the request field's block 0.
    /// </summary>
    public static async Task<int> RegisterAsync(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: register <consoleIp> <passcode> <accountId> [clientIdHex(32B)] [ps4|ps5]");
            return 1;
        }

        string consoleIp = args[1];
        string passcode = args[2];
        string accountId = args[3];
        byte[] clientId = args.Length > 4 && args[4].Length == 64
            ? Convert.FromHexString(args[4])
            : System.Security.Cryptography.RandomNumberGenerator.GetBytes(32); // the 32-byte Client-Type id
        var platform = args.Any(a => a.Equals("ps4", StringComparison.OrdinalIgnoreCase))
            ? HalyardConsolePlatform.Ps4 : HalyardConsolePlatform.Ps5;

        // The same resolver the app uses. The lab used to carry its own copy, and today the identical
        // family-keying bug had to be fixed in both — so one implementation, exercised by two callers.
        IHalyardRegistrationCipher cipher =
            new HalyardRegistrationCipherResolver().Resolve(platform, out string source);
        Console.WriteLine($"registration cipher: available={cipher.IsAvailable}  family={platform}  ({source})");
        if (!cipher.IsAvailable)
        {
            Console.Error.WriteLine($"no registration constants for {platform}; supply docs/protocol/captures/registration_crypto_vectors.json or build with the bundled constants.");
            return 1;
        }

        var request = new HalyardRegistrationRequest(
            ConsoleId: consoleIp,
            ConsoleHost: consoleIp,
            AccountId: accountId,
            Passcode: passcode,
            ClientDeviceId: clientId,
            Platform: platform);

        Console.WriteLine($"pairing {platform} at {consoleIp} (passcode {passcode}, account {accountId}, client-type {Convert.ToHexString(clientId).ToLowerInvariant()})...");
        var client = new HalyardRegistrationClient(cipher);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        HalyardRegistrationResult result = await client.RegisterAsync(request, cts.Token);

        if (!result.Succeeded)
        {
            Console.Error.WriteLine($"registration FAILED: {result.FailureReason}");
            return 1;
        }

        HalyardPairingRecord rec = result.Record!;
        Console.WriteLine("registration SUCCEEDED — pairing record:");
        Console.WriteLine($"  registkey (ascii): {System.Text.Encoding.ASCII.GetString(rec.RegistrationKey)}");
        Console.WriteLine($"  registkey (hex)  : {Convert.ToHexString(rec.RegistrationKey).ToLowerInvariant()}");
        Console.WriteLine($"  companion (RP-Key): {Convert.ToHexString(rec.Companion).ToLowerInvariant()}");
        Console.WriteLine($"  keytype          : {rec.KeyType}");
        Console.WriteLine("  (persist rec.Serialize() as the credential blob for later sessions)");
        return 0;
    }

    // ---- Stage 2: LAN discovery ----

    /// <summary>
    /// Probes every console family, not just PS5: the two families listen on different ports with different
    /// protocol versions (PS5 9302/00030010, PS4 987/00020020), so one service per
    /// <see cref="HalyardDiscoveryProfile.All"/> entry is the only way to see both. A single-profile sweep
    /// silently omits the other family rather than reporting it as absent.
    /// </summary>
    public static async Task<int> DiscoverAsync()
    {
        string ports = string.Join(" + ", HalyardDiscoveryProfile.All.Select(p => $"{p.HostType} {p.DiscoveryPort}"));
        Console.WriteLine($"Broadcasting SRCH probes ({ports})...");

        var found = new List<(HalyardDiscoveryProfile Profile, DiscoveredConsole Console)>();
        var subscriptions = new List<IDisposable>();
        var completions = new List<Task>();

        foreach (HalyardDiscoveryProfile profile in HalyardDiscoveryProfile.All)
        {
            var discovery = new HalyardLanDiscoveryService(profile: profile);
            var done = new TaskCompletionSource();
            HalyardDiscoveryProfile captured = profile;
            subscriptions.Add(discovery.Discover(CancellationToken.None).Subscribe(
                new Observer<DiscoveredConsole>(
                    c => { lock (found) found.Add((captured, c)); },
                    done.SetResult)));
            completions.Add(done.Task);
        }

        try
        {
            await Task.WhenAll(completions).WaitAsync(TimeSpan.FromSeconds(8));
        }
        catch (TimeoutException)
        {
            Console.WriteLine("(one or more probes did not complete in time; reporting what answered)");
        }
        finally
        {
            foreach (IDisposable s in subscriptions)
                s.Dispose();
        }

        if (found.Count == 0)
        {
            Console.WriteLine("No consoles responded on any family.");
            return 0;
        }

        foreach ((HalyardDiscoveryProfile profile, DiscoveredConsole console) in found)
        {
            Console.WriteLine($"  [{profile.HostType}] {console.DisplayName}  {console.IpAddress}  awake={console.IsAwake}  id={console.Id}");
        }

        return 0;
    }

    // ---- Stage 0: demux verification ----

    public static Task<int> ReplayAsync(string[] args)
    {
        if (args.Length >= 2 && args[1].Equals("synth", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ReplaySynthetic());
        }

        if (args.Length >= 2 && File.Exists(args[1]))
        {
            return Task.FromResult(ReplayFile(args[1]));
        }

        Console.Error.WriteLine("usage: replay synth | replay <file>");
        return Task.FromResult(1);
    }

    private static int ReplaySynthetic()
    {
        var demuxer = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        int videoFrames = 0, audioFrames = 0;
        demuxer.VideoFrameReady += f => { videoFrames++; Console.WriteLine($"  video frame: {f.Payload.Length} bytes, key={f.IsKeyFrame}"); };
        demuxer.AudioFrameReady += f => { audioFrames++; Console.WriteLine($"  audio frame: {f.Payload.Length} bytes"); };

        uint seq = 0;
        // Frame 0: keyframe across 3 units (unitsTotal > 8 marks it key).
        demuxer.Ingest(SynthVideo(seq++, frameIndex: 0, unitIndex: 0, unitsTotal: 12, payload: Bytes(0x11, 1200)));
        demuxer.Ingest(SynthVideo(seq++, frameIndex: 0, unitIndex: 1, unitsTotal: 12, payload: Bytes(0x22, 1200)));
        demuxer.Ingest(SynthVideo(seq++, frameIndex: 0, unitIndex: 2, unitsTotal: 12, payload: Bytes(0x33, 300)));
        // Audio interleaved.
        demuxer.Ingest(SynthAudio(seq++, Bytes(0x44, 280)));
        // Frame 1: inter frame (flushes frame 0).
        demuxer.Ingest(SynthVideo(seq++, frameIndex: 1, unitIndex: 0, unitsTotal: 1, payload: Bytes(0x55, 400)));
        // Frame 2 (flushes frame 1).
        demuxer.Ingest(SynthVideo(seq++, frameIndex: 2, unitIndex: 0, unitsTotal: 1, payload: Bytes(0x66, 400)));

        Console.WriteLine($"reassembled {videoFrames} video frame(s), {audioFrames} audio frame(s)");
        return videoFrames >= 2 && audioFrames >= 1 ? 0 : 2;
    }

    private static int ReplayFile(string path)
    {
        var demuxer = new HalyardStreamDemuxer(new PassthroughHalyardSessionCrypto());
        int video = 0, audio = 0, control = 0;
        demuxer.VideoFrameReady += _ => video++;
        demuxer.AudioFrameReady += _ => audio++;
        demuxer.ControlPacketReceived += (_, _) => control++;

        byte[] data = File.ReadAllBytes(path);
        int offset = 0, packets = 0;
        while (offset + 2 <= data.Length)
        {
            int len = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
            offset += 2;
            if (len <= 0 || offset + len > data.Length)
            {
                break;
            }

            demuxer.Ingest(data.AsSpan(offset, len));
            offset += len;
            packets++;
        }

        Console.WriteLine($"replayed {packets} packet(s): {video} video frame(s), {audio} audio frame(s), {control} control packet(s)");
        return 0;
    }

    // ---- Stage 1/2: cloud ----

    public static int PrintAuthUrl()
    {
        HalyardAuthClient auth = BuildAuth(out _);
        Console.WriteLine(auth.BuildAuthorizeUrl(HalyardClientDeviceId.For(new DefaultDeviceIdentity())));
        return 0;
    }

    /// <summary>
    /// The full first-run sign-in, without a browser: print the URL, take the redirect back on stdin.
    ///
    /// <para>
    /// The same conversation the app's web view has, driven by hand — which is the point of the seam being
    /// shaped as "here is a URL" / "here is where it landed" rather than as something that opens a window. It
    /// also makes the exchange debuggable without launching the UI, which is how the rest of this harness earns
    /// its keep.
    /// </para>
    /// </summary>
    public static async Task<int> SignInAsync()
    {
        var gateway = BuildGateway();
        if (!gateway.CanSignIn)
        {
            Console.Error.WriteLine("set RIPCORD_CLIENT_ID and RIPCORD_CLIENT_SECRET first.");
            return 1;
        }

        Console.WriteLine("Open this in a browser and sign in:");
        Console.WriteLine();
        Console.WriteLine(gateway.BeginSignIn());
        Console.WriteLine();
        Console.WriteLine("When the browser lands on a blank remoteplay/redirect page, paste the full URL here:");
        Console.Write("> ");

        string? line = Console.ReadLine();
        if (!Uri.TryCreate((line ?? string.Empty).Trim(), UriKind.Absolute, out Uri? redirected))
        {
            Console.Error.WriteLine("that is not a URL.");
            return 1;
        }

        HalyardAccount account = await gateway.CompleteSignInAsync(redirected, CancellationToken.None);
        Console.WriteLine($"signed in as {account.OnlineId} (region {account.Region}); refresh token stored.");
        Console.WriteLine($"account id: {account.AccountId}   <- this is what pairing needs");
        return 0;
    }

    public static async Task<int> CloudAsync()
    {
        var gateway = BuildGateway();

        // Two ways in, because both are useful: a token stored by `signin`, or one supplied out of band, which
        // is how this command worked before there was any way to sign in at all.
        HalyardAccount? account = await gateway.RestoreAsync(CancellationToken.None);
        if (account is null)
        {
            string? refreshToken = Environment.GetEnvironmentVariable("RIPCORD_REFRESH_TOKEN");
            if (string.IsNullOrEmpty(refreshToken))
            {
                Console.Error.WriteLine(
                    "not signed in. Run `signin` first, or set RIPCORD_REFRESH_TOKEN (plus the client env vars).");
                return 1;
            }

            HalyardAuthClient auth = BuildAuth(out HttpClient http);
            var tokens = new HalyardTokenProvider(auth);
            await tokens.SeedFromRefreshTokenAsync(refreshToken, CancellationToken.None);
            return await ListConsolesAsync(new HalyardCloudClient(http, tokens));
        }

        Console.WriteLine($"signed in as {account.OnlineId} (region {account.Region})");
        Console.WriteLine($"account id: {account.AccountId}");
        return await ListConsolesAsync(gateway.Cloud);
    }

    private static async Task<int> ListConsolesAsync(HalyardCloudClient cloud)
    {
        IReadOnlyList<HalyardConsoleClient> consoles = await cloud.ListConsolesAsync(CancellationToken.None);
        foreach (HalyardConsoleClient console in consoles)
        {
            Console.WriteLine(
                $"  {console.Device.Name}  remotePlay={console.RemotePlayEnabled}  canWake={console.CanWake}  duid={console.Duid}");
        }

        return 0;
    }

    /// <summary>Wake a console through the account service, by the duid the `cloud` command prints.</summary>
    public static async Task<int> CloudWakeAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: cloudwake <duid>   (run `cloud` to list them)");
            return 1;
        }

        var gateway = BuildGateway();
        if (await gateway.RestoreAsync(CancellationToken.None) is null)
        {
            Console.Error.WriteLine("not signed in. Run `signin` first.");
            return 1;
        }

        await new HalyardSessionCoordinator(gateway.Cloud).WakeAsync(args[1], CancellationToken.None);

        // Deliberately not "woken": the command is queued through PSN's own fan-out, and acceptance says
        // nothing about whether the console came up.
        Console.WriteLine("wake command accepted by PSN (whether the console came up is unverified).");
        return 0;
    }

    // ---- Stage 4: direct handshake (stub crypto) ----

    public static async Task<int> ConnectAsync(string[] args)
    {
        if (args.Length < 2 || !IPAddress.TryParse(args[1], out IPAddress? ip))
        {
            Console.Error.WriteLine("usage: connect <ip> [ctrlPort] [strmPort]");
            return 1;
        }

        int ctrlPort = args.Length >= 3 ? int.Parse(args[2]) : 9295;
        int strmPort = args.Length >= 4 ? int.Parse(args[3]) : 9296;

        var parameters = new HalyardConnectionParameters(
            ConsoleId: ip.ToString(),
            ControlEndpoint: new IPEndPoint(ip, ctrlPort),
            StreamEndpoint: new IPEndPoint(ip, strmPort));

        await using var session = new HalyardStreamingSession(
            parameters,
            new HalyardTcpControlChannel(),
            new PassthroughHalyardSessionCrypto(),
            new NullCredentialStore());

        Console.WriteLine($"connecting to {ip}:{ctrlPort} (control) / {strmPort} (stream), stub crypto...");
        var config = new SessionConfig(1280, 720, 60, 10_000, VideoCodec.H264, LatencyMode.Balanced);
        SessionHandshakeResult result = await session.ConnectAsync(config, CancellationToken.None);

        Console.WriteLine(result.Succeeded
            ? "handshake accepted (stream loop running)"
            : $"handshake result: {result.FailureReason}");
        return result.Succeeded ? 0 : 0; // a rejection here is expected until Stage 5; not a lab failure
    }

    // ---- Stage 6: media + input wiring ----

    public static async Task<int> MediaDemoAsync()
    {
        var crypto = new PassthroughHalyardSessionCrypto();
        var pipeline = new CountingDecodePipeline();
        var config = new SessionConfig(1280, 720, 60, 10_000, VideoCodec.H264, LatencyMode.Balanced);
        await pipeline.StartAsync(config, CancellationToken.None);

        // Media path: demuxer -> decode pipeline (the same wiring HalyardStreamingSession performs
        // internally via SessionMediaBridge). Fed here by synthetic stream packets.
        var demuxer = new HalyardStreamDemuxer(crypto);
        demuxer.VideoFrameReady += pipeline.SubmitEncodedVideo;
        demuxer.AudioFrameReady += pipeline.SubmitEncodedAudio;

        uint seq = 0;
        demuxer.Ingest(SynthVideo(seq++, 0, 0, 12, Bytes(0x11, 1200)));
        demuxer.Ingest(SynthVideo(seq++, 0, 1, 12, Bytes(0x22, 300)));
        demuxer.Ingest(SynthAudio(seq++, Bytes(0x44, 280)));
        demuxer.Ingest(SynthVideo(seq++, 1, 0, 1, Bytes(0x55, 400)));
        demuxer.Ingest(SynthVideo(seq++, 2, 0, 1, Bytes(0x66, 400)));
        Console.WriteLine($"decode pipeline received {pipeline.VideoCount} video, {pipeline.AudioCount} audio frame(s)");

        // Input path: neutral controller frames -> feedback packets (state type 6 / history type 1), the
        // outbound half of the wiring (source -> ISessionInputSink -> writer). The first frame establishes
        // the baseline; the second (buttons + sticks changed) yields both a history and a state packet.
        var writer = new HalyardInputPacketWriter(crypto);
        long t0 = DateTime.UtcNow.Ticks;
        writer.BuildPackets(new ControllerStateFrame(
            t0, ControllerButtons.None,
            LeftStickX: 0f, LeftStickY: 0f, RightStickX: 0f, RightStickY: 0f,
            LeftTrigger: 0f, RightTrigger: 0f, Gyro: null, Accel: null, Touchpad: null));
        var frame = new ControllerStateFrame(
            t0 + TimeSpan.TicksPerMillisecond * 5,
            ControllerButtons.South | ControllerButtons.DPadUp,
            LeftStickX: 0.5f, LeftStickY: -0.5f, RightStickX: 0f, RightStickY: 0f,
            LeftTrigger: 0.2f, RightTrigger: 0f, Gyro: null, Accel: null, Touchpad: null);
        IReadOnlyList<byte[]> packets = writer.BuildPackets(frame);

        bool hasState = false, hasHistory = false;
        foreach (byte[] p in packets)
        {
            Console.WriteLine($"input packet built: {p.Length} bytes, type=0x{p[0]:x2}");
            hasState |= p[0] == 0x06;
            hasHistory |= p[0] == 0x01;
        }

        bool ok = pipeline.VideoCount >= 2 && pipeline.AudioCount >= 1 && hasState && hasHistory;
        await pipeline.DisposeAsync();
        return ok ? 0 : 2;
    }

    // ---- helpers ----

    /// <summary>
    /// Drive the cloud rendezvous as far as it currently goes: create the session, trigger the console, and
    /// send the OFFER with our candidates.
    ///
    /// <para>
    /// <b>This does not establish a WAN session, and is not expected to.</b> It exists to make the boundary
    /// observable — everything up to and including the OFFER is implemented and can be run against real
    /// hardware; the console's ANSWER comes back over the push channel, which we neither implement nor have
    /// ever captured. Running this against a real console is how that boundary gets tested rather than assumed,
    /// and it is the harness this project uses for every other protocol stage.
    /// </para>
    /// </summary>
    public static async Task<int> SignalingAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: signaling <duid> [localIp] [port]   (run `cloud` to list duids)");
            return 1;
        }

        var gateway = BuildGateway();
        if (await gateway.RestoreAsync(CancellationToken.None) is not { } account)
        {
            Console.Error.WriteLine("not signed in. Run `signin` first.");
            return 1;
        }

        string localIp = args.Length >= 3 ? args[2] : LocalAddress();
        int port = args.Length >= 4 ? int.Parse(args[3]) : 9296;

        // LOCAL only. The vendor also advertises a STATIC (reflexive) candidate, which requires a STUN client
        // to discover — we have none, so a console off this network has no route to offer back even once the
        // push channel is understood. Stated here because "the OFFER succeeded" would otherwise read as
        // progress toward WAN play that it is not.
        var candidates = new List<HalyardCandidate> { new("LOCAL", localIp, port) };

        var coordinator = new HalyardSessionCoordinator(gateway.Cloud);
        var console = new HalyardConsoleClient(new HalyardDevice(string.Empty, null, null), args[1], "PS5");

        Console.WriteLine($"account {account.AccountId}, console {args[1]}");
        Console.WriteLine($"offering LOCAL candidate {localIp}:{port}");

        HalyardConnectHandle handle = await coordinator.BeginAsync(console, candidates, CancellationToken.None);

        Console.WriteLine($"session {handle.SessionId} created; command sent; OFFER accepted.");
        Console.WriteLine();
        Console.WriteLine("This is as far as the implemented path goes. The console's ANSWER (its own");
        Console.WriteLine("candidates) is delivered over the push WebSocket, which is unimplemented and");
        Console.WriteLine("uncaptured -- see docs/protocol/ps5-cloud-session-api.md. Leaving the session.");

        await coordinator.EndAsync(handle, CancellationToken.None);
        return 0;
    }

    /// <summary>
    /// The full WAN rendezvous, end to end: create session → wake console → gather STUN → OFFER (with retry) →
    /// receive the console's OFFER over the push channel → print its candidates.
    ///
    /// <para>
    /// This is the culmination of the connect path: unlike <see cref="SignalingAsync"/> (which stops at the
    /// OFFER), it opens the real push WebSocket and waits for the console to answer, so a success means we hold
    /// the console's reachable candidates — the last input the direct transport needs. Two things are still
    /// <c>[X]</c> and this is where they get tested against hardware: whether the push upgrade accepts a bearer
    /// token, and whether the console answers an OFFER whose <c>skey</c> is zeroed.
    /// </para>
    /// </summary>
    public static async Task<int> WanConnectAsync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: wanconnect <duid> [timeoutSeconds]   (run `cloud` to list duids)");
            return 1;
        }

        int timeoutSeconds = args.Length >= 3 && int.TryParse(args[2], out int t) ? t : 30;

        var gateway = BuildGateway();
        if (await gateway.RestoreAsync(CancellationToken.None) is not { } account)
        {
            Console.Error.WriteLine("not signed in. Run `signin` first.");
            return 1;
        }

        string token = await gateway.AccessTokenAsync(CancellationToken.None);
        HalyardPushServerInfo pushServer = await gateway.Cloud.GetPushServerAsync(CancellationToken.None);
        Console.WriteLine($"account {account.AccountId}, console {args[1]}");
        Console.WriteLine($"push host {pushServer.Fqdn}, keepalive {pushServer.ClientKeepAlive.TotalSeconds:0}s");
        Console.WriteLine($"rendezvous timeout: {timeoutSeconds}s");

        // The socket the media would stream from — STUN gathers its reflexive mapping on this exact socket.
        using var media = new System.Net.Sockets.UdpClient(new IPEndPoint(IPAddress.Any, 0));
        await using var pushChannel = new HalyardPushChannel(new ClientWebSocketChannel());
        pushChannel.SignalingReceived += m =>
            Console.WriteLine($"  push: {m.Action} reqId={m.ReqId} from={m.FromPlatform} candidates={m.Candidates.Count}");

        var rendezvous = new HalyardWanRendezvous(
            new HalyardCloudSignalingClient(gateway.Cloud), new StunReflexiveGatherer(),
            new HalyardWanRendezvousOptions
            {
                RendezvousTimeout = TimeSpan.FromSeconds(timeoutSeconds),
                Log = line => Console.WriteLine($"  · {line}"),
            });

        Console.WriteLine("starting rendezvous (this opens the push channel and waits for the console to answer)...");
        try
        {
            await using HalyardWanConnection conn = await rendezvous.ConnectAsync(
                new HalyardWanRequest(args[1], account.AccountId), media, pushChannel, pushServer, token,
                CancellationToken.None);

            Console.WriteLine($"CONNECTED. session {conn.SessionId}. console candidates:");
            foreach (HalyardSignalingCandidate c in conn.ConsoleCandidates)
            {
                Console.WriteLine($"    {c.Type}  {c.Address}:{c.Port}");
            }

            Console.WriteLine();
            Console.WriteLine("These are the console's reachable endpoints — the direct transport would connect");
            Console.WriteLine("to one of them next. Leaving the session.");
            return 0;
        }
        catch (HalyardCloudException ex)
        {
            Console.Error.WriteLine($"rendezvous failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>This machine's LAN address, as a default for the candidate we advertise.</summary>
    private static string LocalAddress()
    {
        // Connecting a UDP socket sends nothing, but makes the OS pick the interface it would route from —
        // which is the one worth advertising, and is more reliable than taking the first non-loopback address.
        using var probe = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);
        probe.Connect("8.8.8.8", 65530);
        return ((IPEndPoint)probe.LocalEndPoint!).Address.ToString();
    }

    private static HalyardAuthClient BuildAuth(out HttpClient http)
    {
        http = new HttpClient();
        return new HalyardAuthClient(http, HalyardClientConfigFile.Load());
    }

    /// <summary>
    /// The account gateway over the real on-disk token store, so a `signin` here leaves the app signed in too —
    /// they share <c>account.json</c>, which is what makes this harness useful for reproducing what the app sees.
    ///
    /// <para>
    /// <c>RIPCORD_DEVICE_ID</c> (32 hex chars = the 16-byte device-id tail) overrides the MachineGuid-derived
    /// device identity — a diagnostic for the WAN-join question: the console authorizes a specific registered
    /// remote-play device, and this lets us present a chosen one (e.g. the official app's captured id) to test
    /// whether device identity is the join gate. It only takes effect on a fresh <c>signin</c>, since the duid
    /// is bound into the token at the code exchange.
    /// </para>
    /// </summary>
    private static HalyardAccountGateway BuildGateway()
    {
        IDeviceIdentity identity = new DefaultDeviceIdentity();
        string? overrideHex = Environment.GetEnvironmentVariable("RIPCORD_DEVICE_ID");
        if (!string.IsNullOrWhiteSpace(overrideHex)
            && overrideHex.Length == 32
            && DefaultDeviceIdentity.TryParseMachineGuid(overrideHex, out byte[] bytes))
        {
            identity = new StaticDeviceIdentity(bytes);
            Console.Error.WriteLine($"[device id overridden via RIPCORD_DEVICE_ID: …{overrideHex[^8..]}]");
        }

        return new HalyardAccountGateway(
            new HttpClient(), HalyardClientConfigFile.Load(), new AccountTokenStore(), identity);
    }

    // Synthesize v1 A/V packets (spec §6.1). Video payload starts at offset 21 (base 18 + 3-byte video
    // prefix), audio at 18. unitsTotal/unitsFec pack into the off5..8 dword; keyPos rides at off14.
    private static byte[] SynthVideo(uint seq, ushort frameIndex, byte unitIndex, int unitsTotal, byte[] payload)
    {
        const int payloadOffset = HalyardStreamHeader.BaseLength + 3;
        byte[] packet = new byte[payloadOffset + payload.Length];
        packet[0] = HalyardStreamHeader.TypeVideo;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), (ushort)seq);   // packet_index
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(3), frameIndex);    // frame_index
        uint packed = ((uint)unitIndex << 21) | ((uint)(unitsTotal - 1) << 10); // parity_units = 0
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(5), packed);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(HalyardStreamHeader.KeyPositionOffset), seq * 0x400u);
        payload.CopyTo(packet.AsSpan(payloadOffset));
        return packet;
    }

    private static byte[] SynthAudio(uint seq, byte[] payload)
    {
        const int payloadOffset = HalyardStreamHeader.BaseLength;
        byte[] packet = new byte[payloadOffset + payload.Length];
        packet[0] = HalyardStreamHeader.TypeAudio;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), (ushort)seq);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(HalyardStreamHeader.KeyPositionOffset), seq * 0x100u);
        payload.CopyTo(packet.AsSpan(payloadOffset));
        return packet;
    }

    private static byte[] Bytes(byte value, int count)
    {
        byte[] b = new byte[count];
        Array.Fill(b, value);
        return b;
    }
}
