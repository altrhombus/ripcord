namespace Ripcord.Protocol.Halyard.Common.Crypto;

/// <summary>
/// Inputs to establishing the v1 <em>control-plane</em> crypto (the symmetric <c>/sess/ctrl</c> field
/// cipher). Collected by the session plumbing from the <c>/sess/init</c> response and the stored pairing
/// record; the crypto only runs the transform.
/// </summary>
/// <param name="Nonce">The 16-byte RP-Nonce from the console's <c>/sess/init</c> response.</param>
/// <param name="Companion">The 16-byte companion (the stored RP-Key from pairing).</param>
/// <param name="CodecSelector">The codec/type selector that helps pick the per-field IV context key.</param>
/// <param name="VersionSelector">The protocol/version selector that picks the KDF variant + context key.</param>
public readonly record struct HalyardControlKeyMaterial(
    ReadOnlyMemory<byte> Nonce,
    ReadOnlyMemory<byte> Companion,
    int CodecSelector,
    int VersionSelector);

/// <summary>
/// Where a packet's crypto fields sit, so the per-packet seal/open can locate the key position, the GMAC
/// tag, and the encrypted payload. A/V packets carry the tag at offset 10 and the key position at 14;
/// input/feedback packets carry the tag at offset 8 and the key position at offset 4 (spec §3, §6.1).
/// </summary>
/// <param name="KeyPos">The packet's 64-bit key position (from the header; drives the nonce + rotation).</param>
/// <param name="TagOffset">Byte offset of the 4-byte GMAC tag within the packet.</param>
/// <param name="PayloadOffset">Byte offset where the (encrypted) media/input payload begins.</param>
public readonly record struct HalyardPacketLayout(ulong KeyPos, int TagOffset, int PayloadOffset)
{
    /// <summary>A/V (video/audio) packet: tag at offset 10.</summary>
    public static HalyardPacketLayout Av(ulong keyPos, int payloadOffset) => new(keyPos, 10, payloadOffset);

    /// <summary>Input/feedback packet: tag at offset 8, key position at offset 4.</summary>
    public static HalyardPacketLayout Feedback(ulong keyPos, int payloadOffset) => new(keyPos, 8, payloadOffset);
}

/// <summary>Input to first-time registration (obtaining a registration key for this client+console+account).</summary>
/// <param name="ConsoleId">Discovery identity of the console being paired.</param>
/// <param name="ConsoleHost">The console's address (registration is HTTP/1.1 over TCP 9295, spec §2.0).</param>
/// <param name="AccountId">The PSN account id (obtained via cloud sign-in) that owns the pairing.</param>
/// <param name="Passcode">The 8-digit passcode the user reads off the console (Settings → Remote Play →
/// link device). The top 4 digits key the registration auth (a %04d-formatted vendor registration tag, spec §2.0).</param>
/// <param name="ClientDeviceId">This client's 16-byte device id (RP-Did material).</param>
/// <param name="Platform">Selects the registration endpoint (<c>/sie/ps5/…</c> vs <c>/sie/ps4/…</c>).</param>
public sealed record HalyardRegistrationRequest(
    string ConsoleId,
    string ConsoleHost,
    string AccountId,
    string Passcode,
    ReadOnlyMemory<byte> ClientDeviceId,
    HalyardConsolePlatform Platform = HalyardConsolePlatform.Ps5);

/// <summary>Which console family is being paired - picks the registration endpoint path (spec §2.0).</summary>
public enum HalyardConsolePlatform
{
    Ps5,
    Ps4,
}

/// <summary>
/// Result of first-time registration. On success <see cref="Record"/> is the full pairing record (registkey
/// + companion + keytype) that every later session depends on; it is serialized to the opaque credential blob
/// the store persists.
/// </summary>
public sealed record HalyardRegistrationResult(bool Succeeded, string? FailureReason, HalyardPairingRecord? Record);
