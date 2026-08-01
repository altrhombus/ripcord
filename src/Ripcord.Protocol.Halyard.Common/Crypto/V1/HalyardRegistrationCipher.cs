using System.Security.Cryptography;

namespace Ripcord.Protocol.Halyard.Common.Crypto.V1;

/// <summary>
/// The v1 registration body cipher. One transport key + one per-pairing material protect both directions
/// via the §2.1 field cipher (AES-128-CFB, IV = HMAC-SHA256(contextKey, material‖be64(counter)),
/// contextKey = the PS5 HMAC key, counter 0).
///
/// <para>
/// The request body is <c>[0x1e0-byte context][encrypted field]</c>. The context is random except that the
/// material is transmitted inside it in <em>wrapped</em> form (scattered to two fixed offsets), so the
/// console unwraps it and derives the same IV — no cloud, works offline. The key is
/// <see cref="HalyardRegistrationKdf.DeriveKey"/>; the field plaintext is
/// <c>Client-Type: …\r\nNp-AccountId: …\r\n</c>; the response is the pairing record.
/// </para>
/// </summary>
public sealed class HalyardRegistrationCipher : IHalyardRegistrationCipher
{
    /// <summary>The random request context length; the encrypted field begins here (0x1e0 = 480).</summary>
    public const int ContextLength = 0x1e0;

    /// <summary>Both directions use counter 0 (from live captures).</summary>
    public const ulong FieldCounter = 0;

    private readonly HalyardRegistrationKdf _kdf;
    private readonly byte[] _contextKey; // = the PS5 HMAC key (our "B_eq_1")

    public HalyardRegistrationCipher(HalyardRegistrationKdf kdf, ReadOnlySpan<byte> contextKey)
    {
        _kdf = kdf ?? throw new ArgumentNullException(nameof(kdf));
        if (contextKey.Length != 16)
            throw new ArgumentException("Context key must be 16 bytes.", nameof(contextKey));
        _contextKey = contextKey.ToArray();
    }

    /// <summary>A concrete cipher is always available (unlike the stubbed default).</summary>
    public bool IsAvailable => true;

    // ---- IHalyardRegistrationCipher ----

    public HalyardRegistrationExchange BuildRequest(string passcode, ReadOnlySpan<byte> fieldPlaintext)
    {
        uint pin = HalyardRegistrationKdf.ParsePasscode(passcode);
        byte[] context = RandomNumberGenerator.GetBytes(ContextLength);
        byte[] material = RandomNumberGenerator.GetBytes(16);

        // Carry the material in wrapped form so the console can recover it and derive the same IV.
        byte[] wrapped = _kdf.WrapMaterial(material, context);
        HalyardRegistrationKdf.ScatterWrapped(wrapped, context);

        // Key + IV both read from the (now material-carrying) context; encrypt the field at the tail.
        byte[] field = FieldCrypto(context, pin, material).EncryptField(FieldCounter, fieldPlaintext);

        byte[] body = new byte[ContextLength + field.Length];
        context.CopyTo(body, 0);
        field.CopyTo(body, ContextLength);

        return new HalyardRegistrationExchange
        {
            RequestBody = body,
            Context = context,
            Material = material,
            Passcode = passcode,
        };
    }

    public byte[] DecryptResponse(HalyardRegistrationExchange exchange, ReadOnlySpan<byte> responseBody)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        uint pin = HalyardRegistrationKdf.ParsePasscode(exchange.Passcode);
        return FieldCrypto(exchange.Context, pin, exchange.Material).DecryptField(FieldCounter, responseBody);
    }

    // ---- low-level helpers (used by tests / the console-side path) ----

    /// <summary>Recover the material the sender transmitted, from the wrapped bytes in the request context.</summary>
    public byte[] RecoverMaterial(ReadOnlySpan<byte> context)
        => _kdf.UnwrapMaterial(HalyardRegistrationKdf.GatherWrapped(context), context);

    /// <summary>Decrypt a body given the context, passcode, and material explicitly (used by live-vector tests
    /// and the console-side path, where the context, PIN, and recovered material are all known directly).</summary>
    public byte[] DecryptWith(ReadOnlySpan<byte> context, uint passcode, ReadOnlySpan<byte> material, ReadOnlySpan<byte> body)
        => FieldCrypto(context, passcode, material).DecryptField(FieldCounter, body);

    private HalyardControlFieldCrypto FieldCrypto(ReadOnlySpan<byte> context, uint pin, ReadOnlySpan<byte> material)
        => new(_kdf.DeriveKey(context, pin), material, _contextKey);
}
