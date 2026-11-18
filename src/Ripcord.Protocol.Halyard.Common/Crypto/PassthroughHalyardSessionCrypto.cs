namespace Ripcord.Protocol.Halyard.Common.Crypto;

/// <summary>
/// Identity implementation of the crypto seam: no key agreement, no encryption. Control field crypto and
/// the streaminfo cipher copy bytes through unchanged; per-packet open/seal leave the payload as-is and do
/// not compute a real tag. It lets the whole connect pipeline - transport, /sess framing, stream demux,
/// input serialization - run end to end against captures and synthetic (already-plaintext) data without
/// the real key derivation.
///
/// Against a real console this is rejected (the console's MAC/decrypt won't agree). Against replayed
/// plaintext fixtures the pipeline works.
/// </summary>
public sealed class PassthroughHalyardSessionCrypto : IHalyardSessionCrypto
{
    public bool IsControlEstablished { get; private set; }
    public bool IsStreamEstablished { get; private set; }

    public void EstablishControl(in HalyardControlKeyMaterial material) => IsControlEstablished = true;

    public byte[] EncryptControlField(ulong counter, ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

    public byte[] DecryptControlField(ulong counter, ReadOnlySpan<byte> ciphertext) => ciphertext.ToArray();

    public byte[] CryptStreaminfo(ulong counter, ReadOnlySpan<byte> data) => data.ToArray();

    public byte[] GenerateEphemeralPublicKey(int protocolVersion)
    {
        int length = protocolVersion is >= 0x0d and <= 0x11 ? 133 : 65;
        var pub = new byte[length];
        pub[0] = 0x04;
        return pub;
    }

    public byte[] ComputeEcdhSignature(ReadOnlySpan<byte> handshakeKey, ReadOnlySpan<byte> publicKey) => new byte[32];

    public bool VerifyEcdhSignature(ReadOnlySpan<byte> handshakeKey, ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> signature) => true;

    public bool TryEstablishStream(ReadOnlySpan<byte> peerPublicKey, ReadOnlySpan<byte> handshakeKey)
    {
        IsStreamEstablished = true;
        return true;
    }

    // Payload is already plaintext in fixtures: opening succeeds and leaves it in place; sealing is a no-op.
    public bool TryOpenPacket(Span<byte> packet, in HalyardPacketLayout layout) => true;

    public void SealPacket(Span<byte> packet, in HalyardPacketLayout layout) { }

    public void SealControlMessage(Span<byte> packet) { }

    public void SealLossReport(Span<byte> packet) { }

    public void SealInputPacket(Span<byte> packet, int payloadOffset) { }
}

/// <summary>A registration that never contacts a console: every attempt returns a failed result with a reason.</summary>
public sealed class UnavailableHalyardRegistration : IHalyardRegistration
{
    public Task<HalyardRegistrationResult> RegisterAsync(HalyardRegistrationRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new HalyardRegistrationResult(
            Succeeded: false,
            FailureReason: "First-time registration is not yet implemented (spec §2.0).",
            Record: null));
}
