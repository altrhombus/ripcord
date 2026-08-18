using Ripcord.Core.Platform;

namespace Ripcord.Cloud.Halyard;

/// <summary>
/// Builds the <c>duid</c> — the client device unique id — that the authorize and token calls carry.
///
/// <para>
/// <b>Structure, from our own sign-in capture:</b> 48 hex characters, being an 8-byte constant prefix followed
/// by this machine's 16-byte identifier (the same one <see cref="IDeviceIdentity"/> already supplies for the
/// direct console protocol). The prefix is generic — identical for every PC client, carrying no account or
/// machine information — which is why it sits in code as a protocol constant rather than in the dirty room; the
/// trailing 16 bytes are per-machine and are generated here, never copied from anywhere.
/// </para>
///
/// <para>
/// <b>[X] The prefix's meaning is assumed, not confirmed.</b> <c>00000007 00410080</c> reads like a device-type
/// or platform tag paired with a capability word, but that is inference from a single observation and nothing has
/// tested it — no capture varies it, and we have never made the console or the cloud reject a modified one. The
/// value is <c>[C]</c> captured; the interpretation is not. Do not describe it as a known field.
/// </para>
/// </summary>
public static class HalyardClientDeviceId
{
    /// <summary>
    /// The observed 8-byte prefix, as hex. A required on-wire value: the token endpoint is given a <c>duid</c> of
    /// this shape and we have no evidence any other shape is accepted.
    /// </summary>
    public const string Prefix = "0000000700410080";

    /// <summary>Total length of the encoded id, in hex characters (8-byte prefix + 16-byte device id).</summary>
    public const int HexLength = 48;

    /// <summary>
    /// Build the id for <paramref name="identity"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// When the host cannot supply a device id. Thrown rather than substituted with zeros or a random value:
    /// the id is expected to be stable across runs, and a client that quietly presents a different one each
    /// launch would accumulate device registrations against the account with no way to tell why.
    /// </exception>
    public static string For(IDeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        ReadOnlyMemory<byte> id = identity.StableDeviceId;
        if (id.Length != 16)
        {
            throw new InvalidOperationException(
                $"This machine did not supply a stable 16-byte device id (got {id.Length} bytes), so a client "
                + "device id cannot be formed. Supply one explicitly via IDeviceIdentity.");
        }

        return Prefix + Convert.ToHexString(id.Span).ToLowerInvariant();
    }
}
