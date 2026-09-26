// The Rust engine from .NET, through its generated bindings: a layout check, a differential run against
// the managed reference (HalyardPacketCrypto, what the Windows client ships today), the demuxer's
// callbacks as Phase 4 will host them, and the per-packet benchmark on both engines.
//
//   dotnet run -c Release --project engine/hosts/dotnet/Ripcord.Engine.Harness [check|bench]

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ripcord.Engine.Native;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;

[assembly: DisableRuntimeMarshalling]

var mode = args.Length > 0 ? args[0] : "all";
var failures = 0;

if (mode is "all" or "check")
{
    failures += Checks.Layout();
    failures += Checks.DifferentialAgainstManaged();
    failures += Checks.DemuxThroughCallbacks();
    Console.WriteLine(failures == 0 ? "checks: all passed" : $"checks: {failures} FAILED");
}

if (mode is "all" or "bench")
{
    Console.WriteLine($"{RuntimeInformation.OSDescription}, {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}");
    Bench.Run();
}

return failures == 0 ? 0 : 1;

static unsafe class Checks
{
    static int Fail(string what)
    {
        Console.WriteLine($"FAIL {what}");
        return 1;
    }

    /// <summary>engine-plan.md rule 4: compare the library's view of every crossing struct with ours.</summary>
    public static int Layout()
    {
        var f = 0;
        const uint expectedApi = 2;
        if (NativeMethods.ripcord_api_version() != expectedApi)
            f += Fail($"api version {NativeMethods.ripcord_api_version()}, bindings expect {expectedApi}");
        (RipcordStructId id, int size)[] structs =
        [
            (RipcordStructId.StreamHeader, sizeof(RipcordStreamHeader)),
            (RipcordStructId.DemuxSink, sizeof(RipcordDemuxSink)),
            (RipcordStructId.DemuxCounters, sizeof(RipcordDemuxCounters)),
            (RipcordStructId.EcdhBackend, sizeof(RipcordEcdhBackend)),
            (RipcordStructId.KatResult, sizeof(RipcordKatResult)),
            (RipcordStructId.ScriptedConsoleCounts, sizeof(RipcordScriptedConsoleCounts)),
        ];
        foreach (var (id, size) in structs)
        {
            var native = (int)NativeMethods.ripcord_struct_size((uint)id);
            if (native != size)
                f += Fail($"{id}: the library says {native} bytes, .NET lays it out in {size}");
        }
        Console.WriteLine($"layout: api {NativeMethods.ripcord_api_version()}, {structs.Length} structs agree");
        return f;
    }

    /// <summary>
    /// Every packet sealed by one engine must verify and decrypt identically in the other, across key
    /// positions that cross rotation windows and the 32-bit edge, for both AAD rules.
    /// </summary>
    public static int DifferentialAgainstManaged()
    {
        var f = 0;
        var rng = new Random(20260925);
        var cases = 0;
        for (var keySet = 0; keySet < 8; keySet++)
        {
            var key = new byte[16];
            var iv = new byte[16];
            rng.NextBytes(key);
            rng.NextBytes(iv);
            using var managed = new HalyardPacketCrypto(key, iv);
            var native = New(key, iv);
            try
            {
                ulong[] positions = [0, 16, 44_999, 45_000, 45_001, 719_984, 720_000, 4_294_967_295, 4_294_967_296, (ulong)rng.NextInt64()];
                foreach (var keyPos in positions)
                {
                    cases++;
                    var packet = new byte[rng.Next(24, 1500)];
                    rng.NextBytes(packet);

                    // CTR: both engines produce the same keystream.
                    var viaManaged = managed.CryptPayload(keyPos, packet.AsSpan(21));
                    var viaNative = packet.AsSpan(21).ToArray();
                    fixed (byte* p = viaNative)
                        NativeMethods.ripcord_packet_crypto_crypt_payload(native, keyPos, p, (nuint)viaNative.Length);
                    if (!viaManaged.AsSpan().SequenceEqual(viaNative))
                        f += Fail($"CTR differs at keyPos {keyPos}");

                    // A/V rule: managed seals, native verifies; native seals, managed verifies.
                    var sealedManaged = managed.SealPacket(keyPos, packet);
                    fixed (byte* p = sealedManaged)
                        if (NativeMethods.ripcord_packet_crypto_verify(native, keyPos, p, (nuint)sealedManaged.Length, 10, false) != RipcordStatus.Ok)
                            f += Fail($"native rejects a managed A/V seal at keyPos {keyPos}");
                    var sealedNative = packet.ToArray();
                    fixed (byte* p = sealedNative)
                        NativeMethods.ripcord_packet_crypto_seal(native, keyPos, p, (nuint)sealedNative.Length, 10, false);
                    if (!managed.VerifyPacket(keyPos, sealedNative))
                        f += Fail($"managed rejects a native A/V seal at keyPos {keyPos}");

                    // Control rule (tag at 5, key position zeroed too): compare the tags directly.
                    var tagManaged = managed.ComputeTag(keyPos, packet, tagOffset: 5, zeroKeyPos: true);
                    var sealedControl = packet.ToArray();
                    fixed (byte* p = sealedControl)
                        NativeMethods.ripcord_packet_crypto_seal(native, keyPos, p, (nuint)sealedControl.Length, 5, true);
                    if (!tagManaged.AsSpan().SequenceEqual(sealedControl.AsSpan(5, 4)))
                        f += Fail($"control tags differ at keyPos {keyPos}");

                    // And a tampered packet fails in native.
                    sealedManaged[^1] ^= 0x40;
                    fixed (byte* p = sealedManaged)
                        if (NativeMethods.ripcord_packet_crypto_verify(native, keyPos, p, (nuint)sealedManaged.Length, 10, false) != RipcordStatus.Rejected)
                            f += Fail($"native accepts a tampered packet at keyPos {keyPos}");
                }
            }
            finally
            {
                NativeMethods.ripcord_packet_crypto_free(native);
            }
        }
        Console.WriteLine($"differential: {cases} key positions x 4 checks against HalyardPacketCrypto");
        return f;
    }

    static RipcordPacketCrypto* New(byte[] key, byte[] iv)
    {
        fixed (byte* k = key, i = iv)
            return NativeMethods.ripcord_packet_crypto_new(k, i);
    }

    sealed class Collected
    {
        public readonly List<byte[]> Frames = [];
        public readonly List<(ushort, ushort)> Losses = [];
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnFrame(void* user, byte* data, nuint length, bool isKeyframe) =>
        ((Collected)GCHandle.FromIntPtr((nint)user).Target!).Frames.Add(new ReadOnlySpan<byte>(data, (int)length).ToArray());

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static void OnLoss(void* user, ushort first, ushort last) =>
        ((Collected)GCHandle.FromIntPtr((nint)user).Target!).Losses.Add((first, last));

    /// <summary>
    /// The demuxer hosted as Phase 4 describes: <c>[UnmanagedCallersOnly]</c> statics with a GCHandle as
    /// the user pointer. The packets are sealed by the managed engine, so this is also a differential
    /// check of the native demuxer's crypto seam against the managed crypto.
    /// </summary>
    public static int DemuxThroughCallbacks()
    {
        var f = 0;
        var key = Enumerable.Range(0, 16).Select(i => (byte)(i * 7)).ToArray();
        var iv = Enumerable.Range(0, 16).Select(i => (byte)(0xf0 - i)).ToArray();
        using var managed = new HalyardPacketCrypto(key, iv);
        byte[] slice = [0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x21];

        byte[] Packet(ushort frame, uint keyPosition)
        {
            var p = new byte[18 + 3 + 2 + slice.Length];
            p[0] = 0x02;
            p[3] = (byte)(frame >> 8);
            p[4] = (byte)frame;
            // unit 0 of 1, no parity: total_units - 1 = 0 in bits 20..10
            p[14] = (byte)(keyPosition >> 24);
            p[15] = (byte)(keyPosition >> 16);
            p[16] = (byte)(keyPosition >> 8);
            p[17] = (byte)keyPosition;
            slice.CopyTo(p, 23);
            managed.CryptPayloadInPlace(keyPosition, p.AsSpan(21));
            return managed.SealPacket(keyPosition, p);
        }

        var collected = new Collected();
        var handle = GCHandle.Alloc(collected);
        RipcordStreamDemux* demux;
        fixed (byte* k = key, i = iv)
            demux = NativeMethods.ripcord_stream_demux_new(k, i);
        try
        {
            var sink = new RipcordDemuxSink
            {
                user = (void*)GCHandle.ToIntPtr(handle),
                video_frame = &OnFrame,
                video_loss = &OnLoss,
            };
            byte[] sps = [0x00, 0x00, 0x00, 0x01, 0x67, 0x64];
            fixed (byte* s = sps)
                NativeMethods.ripcord_stream_demux_set_video_header(demux, s, (nuint)sps.Length);

            foreach (var (frame, pos) in new (ushort, uint)[] { (0, 0), (1, 50_000), (3, 100_000) })
            {
                var packet = Packet(frame, pos);
                fixed (byte* p = packet)
                    if (NativeMethods.ripcord_stream_demux_ingest(demux, p, (nuint)packet.Length, &sink) != RipcordStatus.Ok)
                        f += Fail($"ingest of frame {frame} failed");
            }
            RipcordDemuxCounters counters;
            NativeMethods.ripcord_stream_demux_counters(demux, &counters);

            var expected = sps.Concat(slice).ToArray();
            if (collected.Frames.Count != 2 || !collected.Frames.All(fr => fr.AsSpan().SequenceEqual(expected)))
                f += Fail($"expected frames 0 and 1 decrypted with the parameter sets prepended, got {collected.Frames.Count}");
            if (collected.Losses.Count != 1 || collected.Losses[0] != (2, 2))
                f += Fail("expected a single loss report for frame 2");
            if (counters.auth_failures != 0)
                f += Fail($"{counters.auth_failures} packets sealed by the managed engine failed native authentication");
        }
        finally
        {
            NativeMethods.ripcord_stream_demux_free(demux);
            handle.Free();
        }
        Console.WriteLine("demux: managed-sealed packets through native callbacks");
        return f;
    }
}

/// <summary>
/// PacketCryptoBenchmark.swift's workload, on both engines: 256 packets sealed 64 payloads apart (so every
/// packet is in its own GMAC rotation window), then verify plus CTR decrypt in a loop.
/// </summary>
static unsafe class Bench
{
    const int PacketBytes = 1426, HeaderLength = 18, Distinct = 256, Packets = 20_000;

    public static void Run()
    {
        var key = Enumerable.Repeat((byte)0x5a, 16).ToArray();
        var iv = Enumerable.Repeat((byte)0xa5, 16).ToArray();
        var template = Enumerable.Range(0, PacketBytes).Select(i => (byte)(i * 31)).ToArray();

        using var managed = new HalyardPacketCrypto(key, iv);
        var sealedPackets = new byte[Distinct][];
        var positions = new ulong[Distinct];
        ulong position = 0;
        for (var i = 0; i < Distinct; i++)
        {
            sealedPackets[i] = managed.SealPacket(position, template);
            positions[i] = position;
            position += (ulong)(PacketBytes - HeaderLength) * 64;
        }

        RipcordPacketCrypto* native;
        fixed (byte* k = key, v = iv)
            native = NativeMethods.ripcord_packet_crypto_new(k, v);
        var working = new byte[PacketBytes];

        double Time(Func<int, bool> perPacket)
        {
            for (var n = 0; n < 2_000; n++) perPacket(n); // warm up, and let tiered JIT settle
            var failures = 0;
            var sw = Stopwatch.StartNew();
            for (var n = 0; n < Packets; n++)
                if (!perPacket(n)) failures++;
            sw.Stop();
            if (failures != 0) throw new InvalidOperationException("a sealed packet failed to verify: the benchmark is measuring the wrong thing");
            return sw.Elapsed.TotalMicroseconds / Packets;
        }

        var nativeUs = Time(n =>
        {
            var i = n % Distinct;
            sealedPackets[i].CopyTo(working, 0);
            fixed (byte* p = working)
            {
                var ok = NativeMethods.ripcord_packet_crypto_verify(native, positions[i], p, PacketBytes, 10, false) == RipcordStatus.Ok;
                NativeMethods.ripcord_packet_crypto_crypt_payload(native, positions[i], p + HeaderLength, PacketBytes - HeaderLength);
                return ok;
            }
        });
        var managedUs = Time(n =>
        {
            var i = n % Distinct;
            sealedPackets[i].CopyTo(working, 0);
            var ok = managed.VerifyPacket(positions[i], working);
            managed.CryptPayloadInPlace(positions[i], working.AsSpan(HeaderLength));
            return ok;
        });
        NativeMethods.ripcord_packet_crypto_free(native);

        Console.WriteLine($"{Packets} packets of {PacketBytes} bytes, a new rotation window every packet:");
        Console.WriteLine($"  Rust engine via P/Invoke:          {nativeUs,6:F2} us/packet");
        Console.WriteLine($"  managed (HalyardPacketCrypto):     {managedUs,6:F2} us/packet");
    }
}
