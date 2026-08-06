using Ripcord.Core.Security;

namespace Ripcord.Core.Consoles;

/// <summary>
/// The on-disk representation of a pairing-record credential: how it is wrapped for storage and unwrapped for
/// use.
///
/// <para>
/// Split out from <see cref="PairedConsoleStore"/> so the format has one obvious home. Two callers need it and
/// neither should have to reach through a store instance to get at it: the store itself, and the protocol layer
/// that rehydrates a pairing record from a stored blob. Before this split the decode step was a static member of
/// the store, called from the record — a shape that only made sense while all three lived in the same file.
/// </para>
/// </summary>
public static class PairedConsoleBlob
{
    /// <summary>
    /// Marks a blob as ciphertext, distinguishing it from a pre-encryption plaintext hex blob.
    ///
    /// <para>
    /// This prefix is a back-compatibility contract, not an implementation detail: it is how an install that
    /// predates encryption is recognised and read rather than silently treated as unpaired. It is public for the
    /// same reason it must not change.
    /// </para>
    /// </summary>
    public const string ProtectedPrefix = "dpapi:";

    /// <summary>Encrypt a pairing record for storage.</summary>
    public static string Encode(byte[] pairingRecord, ICredentialProtector protector)
    {
        ArgumentNullException.ThrowIfNull(pairingRecord);
        ArgumentNullException.ThrowIfNull(protector);
        return ProtectedPrefix + Convert.ToBase64String(protector.Protect(pairingRecord));
    }

    /// <summary>
    /// Decrypt a stored blob, tolerating a legacy plaintext-hex value so upgrading does not silently drop
    /// existing pairings. Null means undecryptable — treat as not paired.
    /// </summary>
    public static byte[]? Decode(string stored, ICredentialProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);

        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            return stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal)
                ? protector.Unprotect(Convert.FromBase64String(stored[ProtectedPrefix.Length..]))
                : Convert.FromHexString(stored);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
