using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace Ripcord.Core.Net.Stun;

/// <summary>The STUN message classes we send and receive (RFC 5389 §6).</summary>
public enum StunMessageType : ushort
{
    BindingRequest = 0x0001,
    BindingSuccess = 0x0101,
    BindingError = 0x0111,
}

/// <summary>
/// A single STUN message: the 20-byte header plus its attributes, encoded and decoded per RFC 5389.
///
/// <para>
/// Only what a client needs to discover its own reflexive address is implemented: build a Binding Request,
/// read a Binding Response's mapped address. That is deliberately narrow — this is not a general STUN/TURN
/// stack. It is the piece that lets Ripcord fill in the reflexive candidate its signaling OFFER advertises,
/// which is otherwise the one candidate it cannot produce.
/// </para>
///
/// <para>
/// <b>Reflexive gathering does not need the vendor's STUN server.</b> A NAT binding's public address is a
/// property of the NAT, not of which server observes it, so any reachable STUN server answers the question.
/// The vendor's own STUN endpoints are authenticated (they carry <c>USERNAME</c> + <c>MESSAGE-INTEGRITY</c>,
/// per our captures) and belong to its relay tier; this client targets ordinary public servers and does not
/// attempt that auth. Reading a server's response is lenient about it, though — an unknown or
/// integrity-bearing attribute is skipped, not rejected.
/// </para>
/// </summary>
public sealed class StunMessage
{
    /// <summary>The RFC 5389 magic cookie, fixed in bytes 4..8 of every modern STUN message.</summary>
    public const uint MagicCookie = 0x2112A442;

    private const int HeaderLength = 20;
    private const int TransactionIdLength = 12;

    // Attribute types we act on. XOR-MAPPED-ADDRESS is the RFC 5389 form; MAPPED-ADDRESS is the RFC 3489 form
    // some servers still return; 0x8020 is the pre-standardization comprehension-optional XOR form seen in the
    // wild (including the vendor's own relay). All three encode the same reflexive endpoint.
    private const ushort AttrMappedAddress = 0x0001;
    private const ushort AttrXorMappedAddress = 0x0020;
    private const ushort AttrXorMappedAddressLegacy = 0x8020;

    private const byte FamilyIPv4 = 0x01;
    private const byte FamilyIPv6 = 0x02;

    public StunMessage(StunMessageType type, byte[] transactionId, IPEndPoint? mappedAddress = null)
    {
        ArgumentNullException.ThrowIfNull(transactionId);
        if (transactionId.Length != TransactionIdLength)
        {
            throw new ArgumentException($"Transaction id must be {TransactionIdLength} bytes.", nameof(transactionId));
        }

        Type = type;
        TransactionId = transactionId;
        MappedAddress = mappedAddress;
    }

    public StunMessageType Type { get; }

    /// <summary>The 12-byte transaction id that ties a response to its request.</summary>
    public byte[] TransactionId { get; }

    /// <summary>The reflexive endpoint from a response's (XOR-)MAPPED-ADDRESS, or null.</summary>
    public IPEndPoint? MappedAddress { get; }

    /// <summary>A Binding Request with a fresh random transaction id.</summary>
    public static StunMessage CreateBindingRequest()
        => new(StunMessageType.BindingRequest, RandomNumberGenerator.GetBytes(TransactionIdLength));

    /// <summary>Encode this message to its wire bytes. Only request encoding carries no attributes.</summary>
    public byte[] ToBytes()
    {
        byte[] buffer = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0), (ushort)Type);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), 0); // no attributes on the requests we send
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), MagicCookie);
        TransactionId.CopyTo(buffer.AsSpan(8));
        return buffer;
    }

    /// <summary>
    /// Parse a datagram as a STUN message, or null when it is not one. Returns null rather than throwing on
    /// anything malformed: a UDP socket receives whatever the network sends, and a stray non-STUN datagram is
    /// an ordinary event, not an exception.
    /// </summary>
    public static StunMessage? TryParse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderLength)
        {
            return null;
        }

        ushort rawType = BinaryPrimitives.ReadUInt16BigEndian(data);
        ushort attributesLength = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        uint cookie = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);

        // The two most-significant bits of a STUN message type are always zero; that plus the cookie is the
        // standard "is this STUN" check that keeps us from misreading an arbitrary datagram.
        if ((rawType & 0xC000) != 0 || cookie != MagicCookie)
        {
            return null;
        }

        if (HeaderLength + attributesLength > data.Length)
        {
            return null;
        }

        byte[] transactionId = data.Slice(8, TransactionIdLength).ToArray();
        IPEndPoint? mapped = ReadMappedAddress(data.Slice(HeaderLength, attributesLength), transactionId);

        return new StunMessage((StunMessageType)rawType, transactionId, mapped);
    }

    private static IPEndPoint? ReadMappedAddress(ReadOnlySpan<byte> attributes, ReadOnlySpan<byte> transactionId)
    {
        int offset = 0;
        IPEndPoint? plain = null;

        while (offset + 4 <= attributes.Length)
        {
            ushort attrType = BinaryPrimitives.ReadUInt16BigEndian(attributes[offset..]);
            ushort attrLength = BinaryPrimitives.ReadUInt16BigEndian(attributes[(offset + 2)..]);
            int valueStart = offset + 4;
            if (valueStart + attrLength > attributes.Length)
            {
                break;
            }

            ReadOnlySpan<byte> value = attributes.Slice(valueStart, attrLength);

            switch (attrType)
            {
                case AttrXorMappedAddress:
                case AttrXorMappedAddressLegacy:
                    // XOR form is preferred: it is what a modern server returns and it survives NATs that
                    // rewrite an address they find verbatim in the payload. Take it immediately.
                    if (TryReadAddress(value, xor: true, transactionId, out IPEndPoint? xored))
                    {
                        return xored;
                    }

                    break;

                case AttrMappedAddress:
                    // Keep the plain form as a fallback but keep scanning in case an XOR form follows.
                    if (plain is null && TryReadAddress(value, xor: false, transactionId, out IPEndPoint? p))
                    {
                        plain = p;
                    }

                    break;
            }

            // Attributes are padded to a 4-byte boundary; the padding is not counted in the length.
            offset = valueStart + attrLength + ((4 - (attrLength & 3)) & 3);
        }

        return plain;
    }

    private static bool TryReadAddress(
        ReadOnlySpan<byte> value, bool xor, ReadOnlySpan<byte> transactionId, out IPEndPoint? endpoint)
    {
        endpoint = null;

        // Layout: 1 reserved byte, 1 family byte, 2 port bytes, then 4 (IPv4) or 16 (IPv6) address bytes.
        if (value.Length < 4)
        {
            return false;
        }

        byte family = value[1];
        int addressLength = family switch
        {
            FamilyIPv4 => 4,
            FamilyIPv6 => 16,
            _ => 0,
        };

        if (addressLength == 0 || value.Length < 4 + addressLength)
        {
            return false;
        }

        ushort port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
        byte[] address = value.Slice(4, addressLength).ToArray();

        if (xor)
        {
            // Port is XORed with the top 16 bits of the cookie; the address with the cookie followed by the
            // transaction id (only the cookie's four bytes matter for IPv4).
            port ^= (ushort)(MagicCookie >> 16);

            Span<byte> mask = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(mask, MagicCookie);
            transactionId.CopyTo(mask[4..]);
            for (int i = 0; i < address.Length; i++)
            {
                address[i] ^= mask[i];
            }
        }

        endpoint = new IPEndPoint(new IPAddress(address), port);
        return true;
    }
}
