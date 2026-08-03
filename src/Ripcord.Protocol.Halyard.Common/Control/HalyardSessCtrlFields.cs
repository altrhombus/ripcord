using System.Buffers.Binary;
using System.Text;
using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Protocol.Halyard.Common.Control;

/// <summary>
/// Builds the encrypted <c>/sess/ctrl</c> header fields (spec §2.1). Each field's plaintext is assembled
/// per the field table, then sealed via <see cref="IHalyardSessionCrypto.EncryptControlField"/> (AES-128-CFB
/// with the per-field counter) and base64-encoded for the HTTP header. Field order fixes the counter:
/// RP-Auth=0, RP-Did=1, RP-OSType=2, RP-StartBitrate=3, RP-StreamingType=4.
///
/// The plaintext constructions (registkey zero-padding, the RP-Did length prefix, the "WinX.Y" string) are
/// the confirmed [V] shapes; the RP-StartBitrate / RP-StreamingType integer encodings are tentative (they
/// don't appear in our verified vectors).
/// </summary>
public static class HalyardSessCtrlFields
{
    public const ulong CounterAuth = 0;
    public const ulong CounterDid = 1;
    public const ulong CounterOsType = 2;
    public const ulong CounterStartBitrate = 3;
    public const ulong CounterStreamingType = 4;

    /// <summary>
    /// The login-passcode field counter. It is <b>5</b> because the per-connection field counter is a single
    /// running value shared across the whole /sess/ctrl TCP connection, and the passcode — sent on the binary
    /// control channel that connection becomes — is the sixth field-encrypt after the five request headers
    /// above. Derived from cap50: the payload decrypts to the typed digits only at counter 5.
    /// </summary>
    public const ulong CounterLoginPin = 5;

    /// <summary>
    /// Login-passcode plaintext: the digits as their ASCII characters, nothing more. A 4-digit PIN is 4 bytes
    /// (e.g. "1234" → <c>31 32 33 34</c>); the field cipher is a stream mode so the ciphertext is the same
    /// length. Rejects non-digits rather than encrypt something the console will never accept.
    /// </summary>
    public static byte[] BuildLoginPinPlaintext(string pin)
    {
        ArgumentException.ThrowIfNullOrEmpty(pin);
        foreach (char c in pin)
        {
            if (c is < '0' or > '9')
            {
                throw new ArgumentException("Login passcode must be digits only.", nameof(pin));
            }
        }

        return System.Text.Encoding.ASCII.GetBytes(pin);
    }

    /// <summary>RP-Auth plaintext: the raw registration key (8 bytes) zero-padded to 16 bytes.</summary>
    public static byte[] BuildAuthPlaintext(ReadOnlySpan<byte> registrationKey)
    {
        var plaintext = new byte[16];
        registrationKey[..Math.Min(registrationKey.Length, 16)].CopyTo(plaintext);
        return plaintext;
    }

    /// <summary>The 32-byte device-id length (RP-Did plaintext), wire-confirmed.</summary>
    public const int DidSize = 32;

    // The device id is a fixed 32-byte structure (NOT a length-prefixed value): a 10-byte marker prefix,
    // then 16 identifying bytes (we use the hex-decoded MachineGuid), then 6 zero bytes. Prefix read from
    // the vendor wire (cap22). The console treats the middle as an opaque client id.
    private static readonly byte[] DidPrefix = [0x00, 0x18, 0x00, 0x00, 0x00, 0x07, 0x00, 0x40, 0x00, 0x80];
    private const int DidSuffixLength = 6;

    /// <summary>RP-Did plaintext: the 32-byte device-id structure (prefix + device bytes + zero suffix).</summary>
    public static byte[] BuildDidPlaintext(ReadOnlySpan<byte> deviceId)
    {
        var did = new byte[DidSize];
        DidPrefix.CopyTo(did.AsSpan());
        int middle = DidSize - DidPrefix.Length - DidSuffixLength; // 16
        int n = Math.Min(deviceId.Length, middle);
        deviceId[..n].CopyTo(did.AsSpan(DidPrefix.Length));
        return did; // trailing suffix bytes remain zero
    }

    /// <summary>RP-OSType plaintext: the ASCII string "Win{major}.{minor}" with a trailing NUL.</summary>
    public static byte[] BuildOsTypePlaintext(int major, int minor)
        => Encoding.ASCII.GetBytes($"Win{major}.{minor}\0");

    /// <summary>A 4-byte little-endian integer field (RP-StartBitrate / RP-StreamingType), matching the wire.</summary>
    public static byte[] BuildInt32Plaintext(int value)
    {
        var plaintext = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(plaintext, value);
        return plaintext;
    }

    /// <summary>
    /// Build all five encrypted field header (name, value) pairs, in counter order, using an established
    /// control-plane crypto. The caller adds them to the <c>/sess/ctrl</c> request.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Build(
        IHalyardSessionCrypto crypto,
        ReadOnlySpan<byte> registrationKey,
        ReadOnlySpan<byte> deviceId,
        int osMajor,
        int osMinor,
        int startBitrate,
        int streamingType)
    {
        string Encrypt(ulong counter, ReadOnlySpan<byte> plaintext)
            => Convert.ToBase64String(crypto.EncryptControlField(counter, plaintext));

        return
        [
            new(SessProtocol.HeaderAuth, Encrypt(CounterAuth, BuildAuthPlaintext(registrationKey))),
            new(SessProtocol.HeaderDid, Encrypt(CounterDid, BuildDidPlaintext(deviceId))),
            new(SessProtocol.HeaderOsType, Encrypt(CounterOsType, BuildOsTypePlaintext(osMajor, osMinor))),
            new(SessProtocol.HeaderStartBitrate, Encrypt(CounterStartBitrate, BuildInt32Plaintext(startBitrate))),
            new(SessProtocol.HeaderStreamingType, Encrypt(CounterStreamingType, BuildInt32Plaintext(streamingType))),
        ];
    }
}
