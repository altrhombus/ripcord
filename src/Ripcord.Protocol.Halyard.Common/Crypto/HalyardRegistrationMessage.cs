using System.Buffers.Binary;
using System.Text;

namespace Ripcord.Protocol.Halyard.Common.Crypto;

/// <summary>
/// The structural (non-secret) half of registration (spec §2.0): HTTP/1.1 request framing, request-field
/// plaintext, response splitting, and parsing the console's reply into a <see cref="HalyardPairingRecord"/>.
/// This layer carries no secret and is unit-testable against synthetic fixtures. The encrypted body crypto
/// (the transport key + field cipher) is the separate seam <see cref="IHalyardRegistrationCipher"/>.
///
/// <para>Wire shape is DLL/capture-confirmed: the request line + headers are built by the client's
/// <c>FUN_101ec050</c>/<c>FUN_101ee360</c> exactly as reproduced here; the request body is a random context
/// prefix followed by the encrypted <c>Client-Type</c>/<c>Np-AccountId</c> field; the response is the
/// encrypted pairing record.</para>
/// </summary>
public static class HalyardRegistrationMessage
{
    /// <summary>Registration runs as HTTP/1.1 over TCP on this port (spec §1).</summary>
    public const int Port = 9295;

    /// <summary>The registration endpoint path for a console family (DLL-confirmed: <c>FUN_101ee360</c>).</summary>
    public static string EndpointPath(HalyardConsolePlatform platform) => platform switch
    {
        HalyardConsolePlatform.Ps5 => "/sie/ps5/rp/sess/rgst",
        HalyardConsolePlatform.Ps4 => "/sie/ps4/rp/sess/rgst",
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    /// <summary>The <c>RP-Version</c> header value per family (wire-confirmed: PS5 → 1.0, PS4 → 10.0).</summary>
    public static string RpVersion(HalyardConsolePlatform platform) => platform switch
    {
        HalyardConsolePlatform.Ps5 => "1.0",
        HalyardConsolePlatform.Ps4 => "10.0",
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    /// <summary>
    /// Build the full HTTP/1.1 registration request bytes, given the already-encrypted body and the client's
    /// own LAN address. Header set is wire-confirmed: uppercase <c>HOST</c> = the client's own IP (no port),
    /// <c>User-Agent: remoteplay Windows</c>, <c>Connection: close</c>, <c>RP-Version</c>. There is no
    /// <c>Np-AccountId</c> or <c>Content-Type</c> header — the account id travels inside the encrypted body.
    /// </summary>
    public static byte[] BuildRequest(HalyardRegistrationRequest request, string clientIp, ReadOnlySpan<byte> encryptedBody)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientIp);
        string head = new StringBuilder()
            .Append("POST ").Append(EndpointPath(request.Platform)).Append(" HTTP/1.1\r\n")
            .Append("HOST: ").Append(clientIp).Append("\r\n")
            .Append("User-Agent: remoteplay Windows\r\n")
            .Append("Connection: close\r\n")
            .Append("Content-Length: ").Append(encryptedBody.Length).Append("\r\n")
            .Append("RP-Version: ").Append(RpVersion(request.Platform)).Append("\r\n")
            .Append("\r\n")
            .ToString();

        byte[] headBytes = Encoding.ASCII.GetBytes(head);
        var buf = new byte[headBytes.Length + encryptedBody.Length];
        headBytes.CopyTo(buf, 0);
        encryptedBody.CopyTo(buf.AsSpan(headBytes.Length));
        return buf;
    }

    /// <summary>
    /// The <c>Client-Type</c> value: a FIXED 64-hex-char client identifier, not a per-device value. Every
    /// client sends this exact constant (observed on our own wire in cap19 and cap22); the console validates
    /// it, so it must be verbatim.
    /// </summary>
    public const string ClientTypeHex = "dabfa2ec873de5839bee8d3f4c0239c4282c07c25c6077a2931afcf0adc0d34f";

    /// <summary>
    /// The plaintext of the encrypted request <em>field</em> (the tail of the request body): CRLF
    /// <c>Client-Type: &lt;hex&gt;</c> then <c>Np-AccountId: &lt;base64&gt;</c> lines (wire-confirmed). The
    /// account id is base64 of the numeric PSN id as a little-endian uint64.
    /// </summary>
    public static byte[] BuildRequestFieldPlaintext(HalyardRegistrationRequest request)
    {
        string body = new StringBuilder()
            .Append("Client-Type: ").Append(ClientTypeHex).Append("\r\n")
            .Append("Np-AccountId: ").Append(EncodeAccountId(request.AccountId)).Append("\r\n")
            .ToString();
        return Encoding.ASCII.GetBytes(body);
    }

    /// <summary>
    /// Split a raw HTTP response into status code and body. Returns false if malformed or non-2xx.
    /// </summary>
    public static bool TrySplitResponse(ReadOnlySpan<byte> response, out int statusCode, out byte[] body)
        => TrySplitResponse(response, out statusCode, out body, out _);

    /// <summary>
    /// As above, and also reports the console's own <c>RP-Application-Reason</c> when it sends one.
    ///
    /// <para>
    /// That header is the console explaining itself, and discarding it made every refusal look alike: a
    /// rejected registration reported only its HTTP status, so "wrong key", "wrong transport for this route"
    /// and "no" were the same message. It is a hex application code (e.g. the generic <c>80108bff</c>), and it
    /// is the difference between a diagnosis and another guess.
    /// </para>
    /// </summary>
    public static bool TrySplitResponse(
        ReadOnlySpan<byte> response, out int statusCode, out byte[] body, out string? applicationReason)
    {
        statusCode = 0;
        body = [];
        applicationReason = null;

        int sep = IndexOf(response, "\r\n\r\n"u8);
        if (sep < 0)
            return false;

        string head = Encoding.ASCII.GetString(response[..sep]);
        string[] lines = head.Split("\r\n");
        string[] statusParts = lines[0].Split(' ', 3);
        if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out statusCode))
            return false;

        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("RP-Application-Reason", StringComparison.OrdinalIgnoreCase))
            {
                applicationReason = line[(colon + 1)..].Trim();
                break;
            }
        }

        body = response[(sep + 4)..].ToArray();
        return statusCode is >= 200 and < 300;
    }

    /// <summary>
    /// Parse the decrypted registration response body (CRLF <c>Key: Value</c> lines) into a pairing record.
    /// Returns false if the required fields are absent.
    /// </summary>
    public static bool TryParsePairingRecord(ReadOnlySpan<byte> decryptedBody, out HalyardPairingRecord? record)
    {
        record = null;
        var fields = ParseFields(Encoding.ASCII.GetString(decryptedBody));

        // The console names the registkey field by its own family, so it also tells us which control-KDF
        // variant a later session must use (PS4 = mode 0, PS5 = mode 1).
        HalyardConsolePlatform platform;
        if (fields.TryGetValue("PS5-RegistKey", out string? registKeyStr))
            platform = HalyardConsolePlatform.Ps5;
        else if (fields.TryGetValue("PS4-RegistKey", out registKeyStr))
            platform = HalyardConsolePlatform.Ps4;
        else
            return false;
        if (!fields.TryGetValue("RP-Key", out string? rpKeyStr))
            return false;

        // The RegistKey field is the hex encoding of the raw registration key bytes (wire-confirmed). The 8
        // raw bytes are themselves ASCII hex digits, so the field carries 16 characters: a key whose raw bytes
        // read "1a2b3c4d" arrives as "3161326233633464". (Synthetic example — never put a real key here.)
        // Decode to the raw bytes: that is what /sess/init re-hex-encodes for RP-Registkey and what RP-Auth
        // encrypts (zero-padded to 16). Storing
        // the hex *string* here double-encodes it (a 32-char RP-Registkey the console 403s). Fall back to
        // the raw ASCII only if the value isn't valid hex (defensive; PS5 values always are).
        byte[] registrationKey = TryDecodeHex(registKeyStr.Trim(), out byte[] decodedKey)
            ? decodedKey
            : Encoding.ASCII.GetBytes(registKeyStr.Trim());

        if (!TryDecodeHex(rpKeyStr.Trim(), out byte[] companion) || companion.Length != 16)
            return false;

        int keyType = 0;
        if (fields.TryGetValue("RP-KeyType", out string? keyTypeStr))
            _ = int.TryParse(keyTypeStr.Trim(), out keyType);

        record = new HalyardPairingRecord(registrationKey, companion, keyType, platform);
        return true;
    }

    private static Dictionary<string, string> ParseFields(string body)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in body.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            fields[line[..colon].Trim()] = line[(colon + 1)..].Trim('\r', ' ', '\t');
        }
        return fields;
    }

    private static string EncodeAccountId(string accountId)
    {
        // Numeric PSN id travels base64-encoded as a little-endian uint64 (wire-confirmed).
        if (ulong.TryParse(accountId, out ulong numeric))
        {
            Span<byte> le = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(le, numeric);
            return Convert.ToBase64String(le);
        }
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(accountId));
    }

    private static bool TryDecodeHex(string value, out byte[] bytes)
    {
        bytes = [];
        if (value.Length % 2 != 0)
            return false;
        try
        {
            bytes = Convert.FromHexString(value);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.Slice(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }
}
