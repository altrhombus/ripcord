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
/// the confirmed [V] shapes; the RP-StartBitrate / RP-StreamingType integer encodings are [X] (they
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
    /// The counter the login passcode is encrypted at, by family: 5 for a PS5 (five <c>/sess/ctrl</c>
    /// headers, 0-4) and 4 for a PS4 (four headers, 0-3 - RP-StreamingType is not a PS4 header, it is sent
    /// as a binary frame after the passcode). Both are capture facts from a Frida hook on our own vendor
    /// client (5.5.0.08250); a PS4 passcode submitted at 5 is refused (b469/b470).
    /// </summary>
    public static ulong LoginPinCounter(HalyardConsolePlatform platform)
        => platform == HalyardConsolePlatform.Ps4 ? CounterStreamingType : CounterLoginPin;

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

    // The device id is be16(24), then 24 bytes: four u16 BE header words (0, 7, 0x40, 0x80) and the 16 bytes
    // of the hex-decoded MachineGuid; 6 zero bytes pad it to 32. [C] from our own binary: FUN_101e0d20 formats
    // the header words and the GUID as one hex string (the x64 8.5 build pushes the same four values), and the
    // be16 length is its caller's. cap22's RP-Did decrypts to the same shape [W], but with 0x41 as the third
    // word: the vendor client can be handed a prebuilt id (FUN_101dc480 -> FUN_101e1390), and the 0x40 here
    // is the binary's own default. The console accepts 0x40 in our sessions; what the word means is [X].
    private static readonly byte[] DeviceIdHeader = [0x00, 0x18, 0x00, 0x00, 0x00, 0x07, 0x00, 0x40, 0x00, 0x80];
    private const int DeviceIdPadLength = 6;

    /// <summary>RP-Did plaintext: the 32-byte device-id structure (header + device bytes + zero padding).</summary>
    public static byte[] BuildDidPlaintext(ReadOnlySpan<byte> deviceId)
    {
        var did = new byte[DidSize];
        DeviceIdHeader.CopyTo(did.AsSpan());
        int middle = DidSize - DeviceIdHeader.Length - DeviceIdPadLength; // 16
        int n = Math.Min(deviceId.Length, middle);
        deviceId[..n].CopyTo(did.AsSpan(DeviceIdHeader.Length));
        return did; // the padding stays zero
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
    /// Build the encrypted field header (name, value) pairs, in counter order, using an established
    /// control-plane crypto: five for a PS5, four for a PS4 (no RP-StreamingType). The caller adds them to
    /// the <c>/sess/ctrl</c> request.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Build(
        IHalyardSessionCrypto crypto,
        ReadOnlySpan<byte> registrationKey,
        ReadOnlySpan<byte> deviceId,
        int osMajor,
        int osMinor,
        int startBitrate,
        int streamingType,
        HalyardConsolePlatform platform)
    {
        string Encrypt(ulong counter, ReadOnlySpan<byte> plaintext)
            => Convert.ToBase64String(crypto.EncryptControlField(counter, plaintext));

        var fields = new List<KeyValuePair<string, string>>
        {
            new(SessProtocol.HeaderAuth, Encrypt(CounterAuth, BuildAuthPlaintext(registrationKey))),
            new(SessProtocol.HeaderDid, Encrypt(CounterDid, BuildDidPlaintext(deviceId))),
            new(SessProtocol.HeaderOsType, Encrypt(CounterOsType, BuildOsTypePlaintext(osMajor, osMinor))),
            new(SessProtocol.HeaderStartBitrate, Encrypt(CounterStartBitrate, BuildInt32Plaintext(startBitrate))),
        };

        // RP-StreamingType is PS5-only. On PS4 it is not a /sess/ctrl header at all (the vendor sends a
        // 16-byte field after the passcode instead), so emitting it here would spend counter 4 - where a
        // PS4 then expects the passcode. Leaving it off is what frees counter 4 for the login. See
        // LoginPinCounter and the capture note on CounterLoginPin.
        if (platform != HalyardConsolePlatform.Ps4)
        {
            fields.Add(new(SessProtocol.HeaderStreamingType,
                Encrypt(CounterStreamingType, BuildInt32Plaintext(streamingType))));
        }

        return fields;
    }
}
