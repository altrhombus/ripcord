using System.Buffers.Binary;
using System.Net;
using Ripcord.Core.Discovery;
using Ripcord.Core.Input;
using Ripcord.Core.Sessions;
using Ripcord.Cloud.Halyard;
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
        Console.WriteLine(auth.BuildAuthorizeUrl());
        return 0;
    }

    public static async Task<int> CloudAsync()
    {
        string? refreshToken = Environment.GetEnvironmentVariable("RIPCORD_REFRESH_TOKEN");
        if (string.IsNullOrEmpty(refreshToken))
        {
            Console.Error.WriteLine("set RIPCORD_REFRESH_TOKEN (and client env vars) to use the cloud command.");
            return 1;
        }

        HalyardAuthClient auth = BuildAuth(out HttpClient http);
        var tokens = new HalyardTokenProvider(auth);
        await tokens.SeedFromRefreshTokenAsync(refreshToken, CancellationToken.None);

        var cloud = new HalyardCloudClient(http, tokens);
        HalyardAccountInfo account = await cloud.GetAccountInfoAsync(CancellationToken.None);
        Console.WriteLine($"signed in: region={account.Region}");

        IReadOnlyList<HalyardConsoleClient> consoles = await cloud.ListConsolesAsync(CancellationToken.None);
        foreach (HalyardConsoleClient console in consoles)
        {
            Console.WriteLine($"  {console.Device.Name}  remotePlay={console.RemotePlayEnabled}  canWake={console.CanWake}");
        }

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

    private static HalyardAuthClient BuildAuth(out HttpClient http)
    {
        var config = new HalyardClientConfig(
            ClientId: Environment.GetEnvironmentVariable("RIPCORD_CLIENT_ID") ?? "",
            ClientSecret: Environment.GetEnvironmentVariable("RIPCORD_CLIENT_SECRET") ?? "",
            RedirectUri: Environment.GetEnvironmentVariable("RIPCORD_REDIRECT_URI") ?? "",
            Scopes: HalyardClientConfig.DefaultScopes);
        http = new HttpClient();
        return new HalyardAuthClient(http, config);
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
        uint packed = ((uint)unitIndex << 21) | ((uint)(unitsTotal - 1) << 10); // units_in_frame_fec = 0
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
