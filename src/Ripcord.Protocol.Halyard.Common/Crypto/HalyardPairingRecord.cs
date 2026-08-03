using System.Buffers.Binary;

namespace Ripcord.Protocol.Halyard.Common.Crypto;

/// <summary>
/// The persistent per-console pairing record produced by registration (spec §2.0) and consumed by every
/// later session. It holds the two long-lived secrets the control-plane crypto depends on - the
/// registration key and the companion (RP-Key) - plus the RP-KeyType that selects the KDF variant.
///
/// <para>
/// The neutral <c>IConsoleCredentialStore</c> persists an opaque <c>byte[]</c> blob per console; this type
/// owns the (Halyard-specific) serialization of that blob via <see cref="Serialize"/> /
/// <see cref="TryDeserialize"/>. The blob is the long-lived credential and must be protected at rest by the
/// store (e.g. DPAPI).
/// </para>
/// </summary>
/// <param name="RegistrationKey">The raw registration-key bytes (the 8 ASCII bytes presented as RP-Registkey
/// and used, zero-padded, as the RP-Auth plaintext).</param>
/// <param name="Companion">The 16-byte companion (the RP-Key from pairing) fed into the control KDF.</param>
/// <param name="KeyType">The RP-KeyType selecting the KDF variant (values 1/2/3/4/6 observed).</param>
public sealed record HalyardPairingRecord(
    byte[] RegistrationKey,
    byte[] Companion,
    int KeyType)
{
    private const byte FormatVersion = 1;

    /// <summary>Serialize to the opaque blob the credential store persists.</summary>
    public byte[] Serialize()
    {
        var blob = new byte[1 + 2 + RegistrationKey.Length + 2 + Companion.Length + 4];
        var span = blob.AsSpan();
        span[0] = FormatVersion;
        int o = 1;
        BinaryPrimitives.WriteUInt16BigEndian(span[o..], (ushort)RegistrationKey.Length); o += 2;
        RegistrationKey.CopyTo(span[o..]); o += RegistrationKey.Length;
        BinaryPrimitives.WriteUInt16BigEndian(span[o..], (ushort)Companion.Length); o += 2;
        Companion.CopyTo(span[o..]); o += Companion.Length;
        BinaryPrimitives.WriteInt32BigEndian(span[o..], KeyType);
        return blob;
    }

    /// <summary>
    /// The LAN wake credential: the value the <c>WAKEUP</c> datagram carries as <c>user-credential</c>.
    ///
    /// <para>Derived from the registration key, wire-confirmed against <c>cap49</c>: the RegistKey is 8 ASCII
    /// hex characters (e.g. <c>"1a2b3c4d"</c>), and the credential is that string read as a base-16 integer and
    /// rendered in decimal (<c>439041101</c>). We already hold the raw ASCII bytes in
    /// <see cref="RegistrationKey"/>, so no extra pairing state is needed to wake a console. Parsed as unsigned
    /// because the full 32-bit range is valid and <c>0x80000000</c>+ would overflow a signed int.</para>
    /// </summary>
    public string WakeCredential()
    {
        // The registration key IS the ASCII hex string on the wire; interpret those bytes as text, not as a
        // number already. Any non-hex content is a corrupt record, not something to paper over with a guess.
        string hex = System.Text.Encoding.ASCII.GetString(RegistrationKey);
        uint value = uint.Parse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Deserialize a blob produced by <see cref="Serialize"/>. Returns false on a malformed blob.</summary>
    public static bool TryDeserialize(ReadOnlySpan<byte> blob, out HalyardPairingRecord? record)
    {
        record = null;
        if (blob.Length < 1 || blob[0] != FormatVersion)
        {
            return false;
        }

        int o = 1;
        if (!TryReadLengthPrefixed(blob, ref o, out byte[] registrationKey) ||
            !TryReadLengthPrefixed(blob, ref o, out byte[] companion) ||
            blob.Length - o < 4)
        {
            return false;
        }

        int keyType = BinaryPrimitives.ReadInt32BigEndian(blob[o..]);
        record = new HalyardPairingRecord(registrationKey, companion, keyType);
        return true;
    }

    private static bool TryReadLengthPrefixed(ReadOnlySpan<byte> blob, ref int offset, out byte[] value)
    {
        value = [];
        if (blob.Length - offset < 2)
        {
            return false;
        }

        int len = BinaryPrimitives.ReadUInt16BigEndian(blob[offset..]);
        offset += 2;
        if (blob.Length - offset < len)
        {
            return false;
        }

        value = blob.Slice(offset, len).ToArray();
        offset += len;
        return true;
    }
}
