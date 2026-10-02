namespace Ripcord.Core.Security;

/// <summary>
/// Encrypts long-lived secrets at rest, bound to the current user account. The console pairing record is a
/// durable credential that authenticates this client to the console: stored as plaintext, any process running
/// as the user can lift it and impersonate us. This seam exists so that protection is a platform detail rather
/// than something each store reinvents (or, as before, defers indefinitely).
/// </summary>
public interface ICredentialProtector
{
    /// <summary>
    /// False when this implementation does not actually encrypt (no OS keystore wired up for the platform).
    /// Callers should surface that rather than let the user believe secrets are protected when they are not.
    /// </summary>
    bool IsRealProtection { get; }

    /// <summary>Short human-readable description of the backing mechanism, for diagnostics and settings UI.</summary>
    string Description { get; }

    /// <summary>Encrypt <paramref name="plaintext"/> for the current user.</summary>
    byte[] Protect(ReadOnlySpan<byte> plaintext);

    /// <summary>
    /// Decrypt data produced by <see cref="Protect"/>, or null if it cannot be decrypted — a different user or
    /// machine, a corrupt file, or data written before protection was enabled. Null means "discard and re-pair",
    /// never "crash".
    /// </summary>
    byte[]? Unprotect(ReadOnlySpan<byte> ciphertext);
}

/// <summary>
/// Selects the protector for the current platform. Windows gets DPAPI, and when DPAPI fails its probe it gets
/// <see cref="UnavailableCredentialProtector"/>, which stores nothing rather than plaintext: the shipped client
/// promises encryption at rest (<c>PRIVACY.md</c>), so the only honest fallback is not saving. Other platforms
/// fall back to <see cref="PlaintextCredentialProtector"/> until a keystore backend (libsecret / kwallet) is
/// wired up, and say so via <see cref="ICredentialProtector.Description"/>.
/// </summary>
public static class CredentialProtection
{
    public static ICredentialProtector ForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return DpapiCredentialProtector.IsAvailable()
                ? new DpapiCredentialProtector()
                : new UnavailableCredentialProtector();
        }

        return new PlaintextCredentialProtector();
    }
}

/// <summary>Thrown by <see cref="UnavailableCredentialProtector.Protect"/>: there is no way to encrypt here.</summary>
public sealed class NoCredentialProtectionException()
    : InvalidOperationException("Windows couldn't encrypt the credential, so Ripcord won't save it.");

/// <summary>
/// The Windows fallback when DPAPI is unreachable. It encrypts nothing and stores nothing: <see cref="Protect"/>
/// throws <see cref="NoCredentialProtectionException"/>, so a store either keeps the secret in memory or
/// fails loudly, and <see cref="Unprotect"/> reads nothing back.
/// </summary>
public sealed class UnavailableCredentialProtector : ICredentialProtector
{
    public bool IsRealProtection => false;

    public string Description => "unavailable (Windows could not provide DPAPI)";

    public byte[] Protect(ReadOnlySpan<byte> plaintext) => throw new NoCredentialProtectionException();

    public byte[]? Unprotect(ReadOnlySpan<byte> ciphertext) => null;
}

/// <summary>
/// No-op protector for platforms without a keystore backend yet. Deliberately named so nobody mistakes it for
/// encryption, and reports <see cref="IsRealProtection"/> = false so the UI can warn.
/// </summary>
public sealed class PlaintextCredentialProtector : ICredentialProtector
{
    public bool IsRealProtection => false;

    public string Description => "not encrypted (no OS keystore backend on this platform yet)";

    public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

    public byte[]? Unprotect(ReadOnlySpan<byte> ciphertext) => ciphertext.ToArray();
}
