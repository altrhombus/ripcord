using Ripcord.Core.Consoles;
using Ripcord.Core.Security;
using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Protocol.Halyard.Session;

/// <summary>
/// Rehydrates a Halyard pairing record from a stored console credential.
///
/// <para>
/// This used to be <c>PairedConsole.ToRecord</c>, a method on the record itself. That put a Halyard return type
/// on a type that otherwise knows nothing about PlayStation, which inverted the dependency direction CLAUDE.md
/// sets out — platform-specific depends on shared, never the reverse — and was the one thing preventing the
/// paired-console store from living in Ripcord.Core at all. As an extension method here, the arrow points the
/// right way and the record stays a plain credential holder.
/// </para>
/// </summary>
public static class PairedConsoleRecordExtensions
{
    /// <summary>
    /// Decode and deserialize the pairing record (registkey + companion + keytype) held by
    /// <paramref name="console"/>. Null when the blob cannot be decrypted (a different user or machine, or a
    /// corrupt entry) or does not parse — both mean "treat as not paired", never "throw mid-connect".
    /// </summary>
    public static HalyardPairingRecord? ToPairingRecord(this PairedConsole console, ICredentialProtector protector)
    {
        ArgumentNullException.ThrowIfNull(console);

        byte[]? plain = PairedConsoleBlob.Decode(console.StoredBlob, protector);
        return plain is not null && HalyardPairingRecord.TryDeserialize(plain, out HalyardPairingRecord? record)
            ? record
            : null;
    }
}
