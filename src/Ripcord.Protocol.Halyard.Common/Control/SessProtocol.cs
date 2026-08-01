using System.Text;

namespace Ripcord.Protocol.Halyard.Common.Control;

/// <summary>
/// The HTTP/1.x-style session-establishment exchange carried over the control channel
/// (docs/protocol/ps5-session-establishment.md): POST /sess/rgst, GET /sess/init, GET /sess/ctrl,
/// each a request-line + RP-* headers + optional body. These are HTTP-shaped but hand-built (they
/// ride a custom transport), so we construct/parse them directly rather than via HttpClient.
///
/// The path/header string constants below are required on-wire tokens the console expects, not our
/// own naming. No captured header *values* (keys/tokens/HMACs) are embedded - those come at runtime
/// from the crypto seam and credential store.
/// </summary>
public static class SessProtocol
{
    public const string PathRegister = "/sie/ps5/rp/sess/rgst";
    public const string PathInit = "/sie/ps5/rp/sess/init";
    public const string PathControl = "/sie/ps5/rp/sess/ctrl";

    // Header names (capitalization as observed; the console is tolerant of Rp-/RP- variance).
    public const string HeaderVersion = "RP-Version";
    public const string HeaderSupportCmd = "RP-SupportCmd";
    public const string HeaderRegistKey = "RP-Registkey";
    public const string HeaderPubKey = "RP-Pubkey";
    public const string HeaderHmac = "RP-Hmac";
    public const string HeaderNonce = "RP-Nonce";
    public const string HeaderFeature = "RP-Feature";
    public const string HeaderData = "RP-Data";
    public const string HeaderTag = "RP-Tag";
    public const string HeaderDevAuthChallenge = "RP-DevACha";
    public const string HeaderDevAuthChallengeTag = "RP-DevAChaTag";

    // v1 /sess/ctrl encrypted fields (base64 of AES-128-CFB output; spec §2.1). The order fixes each
    // field's per-connection counter.
    public const string HeaderAuth = "RP-Auth";                   // counter 0
    public const string HeaderDid = "RP-Did";                     // counter 1
    public const string HeaderOsType = "RP-OSType";               // counter 2
    public const string HeaderStartBitrate = "RP-StartBitrate";   // counter 3
    public const string HeaderStreamingType = "RP-StreamingType"; // counter 4

    // Fixed (plaintext) /sess/ctrl headers the console requires alongside the encrypted fields (wire-confirmed).
    public const string HeaderControllerType = "RP-ControllerType";
    public const string HeaderClientType = "RP-ClientType";
    public const string HeaderConPath = "RP-ConPath";
    public const string HeaderPadProcNo = "RP-PadProcNo";

    public const string ProtocolVersion = "1.0";
    public const string DefaultSupportCmd = "130000";
}

public enum SessHttpMethod
{
    Get,
    Post,
}

/// <summary>An outbound /sess request: method, path, HTTP version, ordered headers, optional body.</summary>
public sealed class SessRequest(SessHttpMethod method, string path, string httpVersion = "HTTP/1.1")
{
    private readonly List<KeyValuePair<string, string>> _headers = [];

    public SessHttpMethod Method { get; } = method;
    public string Path { get; } = path;
    public string HttpVersion { get; } = httpVersion;
    public ReadOnlyMemory<byte> Body { get; set; } = ReadOnlyMemory<byte>.Empty;

    public SessRequest Header(string name, string value)
    {
        _headers.Add(new(name, value));
        return this;
    }

    public byte[] Serialize()
    {
        var sb = new StringBuilder();
        string verb = Method == SessHttpMethod.Post ? "POST" : "GET";
        sb.Append(verb).Append(' ').Append(Path).Append(' ').Append(HttpVersion).Append("\r\n");
        foreach (var (name, value) in _headers)
        {
            sb.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        // Content-Length always present (0 for the header-only init/ctrl requests).
        sb.Append("Content-Length: ").Append(Body.Length).Append("\r\n");
        sb.Append("\r\n");

        byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
        if (Body.IsEmpty)
        {
            return head;
        }

        byte[] full = new byte[head.Length + Body.Length];
        head.CopyTo(full, 0);
        Body.Span.CopyTo(full.AsSpan(head.Length));
        return full;
    }
}

/// <summary>A parsed /sess response: status code + headers (case-insensitive) + body.</summary>
public sealed class SessResponse
{
    private SessResponse(int statusCode, IReadOnlyDictionary<string, string> headers, ReadOnlyMemory<byte> body)
    {
        StatusCode = statusCode;
        Headers = headers;
        Body = body;
    }

    public int StatusCode { get; }
    public IReadOnlyDictionary<string, string> Headers { get; }
    public ReadOnlyMemory<byte> Body { get; }

    public bool IsSuccess => StatusCode is >= 200 and < 300;

    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;

    /// <summary>
    /// Parse a complete response from <paramref name="buffer"/>. Returns false if the header block
    /// (terminated by a blank line) hasn't fully arrived yet, so a stream reader can wait for more.
    /// </summary>
    public static bool TryParse(ReadOnlyMemory<byte> buffer, out SessResponse? response, out int consumed)
    {
        response = null;
        consumed = 0;

        ReadOnlySpan<byte> span = buffer.Span;
        int headerEnd = span.IndexOf("\r\n\r\n"u8);
        if (headerEnd < 0)
        {
            return false;
        }

        int bodyStart = headerEnd + 4;
        string headerText = Encoding.ASCII.GetString(span[..headerEnd]);
        string[] lines = headerText.Split("\r\n");
        if (lines.Length == 0)
        {
            return false;
        }

        // Status line: "HTTP/1.1 200 OK"
        string[] statusParts = lines[0].Split(' ', 3);
        if (statusParts.Length < 2 || !int.TryParse(statusParts[1], out int statusCode))
        {
            return false;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon > 0)
            {
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
        }

        int contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var clRaw))
        {
            _ = int.TryParse(clRaw, out contentLength);
        }

        if (buffer.Length < bodyStart + contentLength)
        {
            return false; // body not fully arrived
        }

        response = new SessResponse(statusCode, headers, buffer.Slice(bodyStart, contentLength));
        consumed = bodyStart + contentLength;
        return true;
    }
}
