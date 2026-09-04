using System.Security.Cryptography;
using System.Text;
using Ripcord.Core.Platform;

namespace Ripcord.Cloud.Halyard;

/// <summary>
/// This client's <c>localHashedId</c> — the 20-byte value a peer publishes in its signaling OFFER and then
/// names itself by in the 9303 control prelude.
///
/// <para>
/// <b>The vendor's own derivation is [X] unknown.</b> Its value is stable for one machine across every capture
/// we hold — the same 20 bytes appear in twelve sessions spanning months — so it is computed from something
/// durable rather than drawn fresh per connection. What it is computed <em>over</em> resisted the obvious
/// candidates: SHA-1 and truncated SHA-256/MD5/SHA-512 over the client device id (as bytes and as its hex
/// string, both cases), the account id (as text and as a 64-bit integer either way round), and the pairwise
/// concatenations, all miss.
/// </para>
///
/// <para>
/// <b>So this computes our own, and says so.</b> The value's job, as far as the wire shows, is to let the
/// console match the peer that sent an OFFER with the peer that opens a transport — the console holds no input
/// it could recompute a digest from, since our OFFER carries the id and not its preimage. A value that is
/// stable for this machine and identical in both places should therefore satisfy it. **That is a hypothesis,
/// and the live pairing run is its test**: if a console rejects our prelude while accepting everything before
/// it, this is the first thing to suspect.
/// </para>
///
/// <para>
/// Derived from the client device id so that it needs no new persisted state and cannot drift between the
/// OFFER and the prelude within a session, or between sessions on one machine — which is the property the
/// captures show the real one having.
/// </para>
/// </summary>
public static class HalyardLocalHashedId
{
    /// <summary>Twenty bytes, matching every observed value.</summary>
    public const int Length = 20;

    /// <summary>
    /// The id for <paramref name="identity"/>. Deterministic: the same machine yields the same value every
    /// time, which is what lets the OFFER and the prelude agree.
    /// </summary>
    public static byte[] For(IDeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return For(HalyardClientDeviceId.For(identity));
    }

    /// <summary>The id for an already-formed client device id.</summary>
    public static byte[] For(string clientDeviceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(clientDeviceId);

        // SHA-1 for its length, not its security: this is an identifier the console compares, never a
        // credential, and nothing about the exchange depends on the digest being hard to invert.
        return SHA1.HashData(Encoding.ASCII.GetBytes(clientDeviceId));
    }
}
