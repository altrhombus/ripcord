using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ripcord.Core.Security;

/// <summary>
/// Windows DPAPI (<c>CryptProtectData</c>/<c>CryptUnprotectData</c>) at <c>CRYPTPROTECT_LOCAL_MACHINE</c>-free
/// current-user scope: the ciphertext can only be decrypted by this user on this machine, with no key for the
/// app to store or leak.
///
/// <para>
/// Called through P/Invoke rather than <c>System.Security.Cryptography.ProtectedData</c> so that
/// <c>Ripcord.Core</c> stays dependency-free and target-framework-neutral (<c>net10.0</c>, not
/// <c>net10.0-windows</c>) — the same property that lets the whole core build and test on Linux.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class DpapiCredentialProtector : ICredentialProtector
{
    /// <summary>
    /// Ties the ciphertext to this application. DPAPI mixes it into the key, so a blob protected by Ripcord
    /// cannot be handed to another process on the same account and decrypted by accident.
    /// </summary>
    private static readonly byte[] Entropy = "Ripcord.ConsolePairing.v1"u8.ToArray();

    public bool IsRealProtection => true;

    public string Description => "Windows DPAPI (current user)";

    /// <summary>Whether DPAPI can be reached at all; false makes the factory fall back rather than throw.</summary>
    public static bool IsAvailable()
    {
        try
        {
            // A round trip is the only honest probe — the export existing does not prove the service works.
            var probe = new DpapiCredentialProtector();
            byte[] sealed_ = probe.Protect("probe"u8);
            return probe.Unprotect(sealed_) is { Length: 5 };
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    public byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext, protect: true)
        ?? throw new InvalidOperationException("DPAPI CryptProtectData failed.");

    public byte[]? Unprotect(ReadOnlySpan<byte> ciphertext) => Transform(ciphertext, protect: false);

    /// <summary>
    /// Shared marshalling for both directions. Returns null on failure; the caller decides whether that is
    /// fatal (protect) or a discard-and-re-pair signal (unprotect).
    /// </summary>
    private static unsafe byte[]? Transform(ReadOnlySpan<byte> input, bool protect)
    {
        // DPAPI rejects a null pointer, so an empty input still needs a valid (if unused) address.
        byte[] inputBuffer = input.IsEmpty ? [0] : input.ToArray();
        int inputLength = input.Length;

        DataBlob output = default;
        try
        {
            fixed (byte* inputPtr = inputBuffer)
            fixed (byte* entropyPtr = Entropy)
            {
                var inBlob = new DataBlob { Size = inputLength, Data = (nint)inputPtr };
                var entropyBlob = new DataBlob { Size = Entropy.Length, Data = (nint)entropyPtr };

                bool ok = protect
                    ? CryptProtectData(ref inBlob, 0, ref entropyBlob, 0, 0, CryptProtectUiForbidden, ref output)
                    : CryptUnprotectData(ref inBlob, 0, ref entropyBlob, 0, 0, CryptProtectUiForbidden, ref output);

                if (!ok || output.Data == 0)
                {
                    return null;
                }

                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
        }
        finally
        {
            if (output.Data != 0)
            {
                LocalFree(output.Data);
            }
        }
    }

    /// <summary>Never prompt: this runs on background/store paths where a UI prompt would hang.</summary>
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    // `description` is passed as 0 (no label) in both directions, so it needs no string marshalling.
    [LibraryImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(
        ref DataBlob input,
        nint description,
        ref DataBlob entropy,
        nint reserved,
        nint prompt,
        int flags,
        ref DataBlob output);

    [LibraryImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(
        ref DataBlob input,
        nint description,
        ref DataBlob entropy,
        nint reserved,
        nint prompt,
        int flags,
        ref DataBlob output);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static partial nint LocalFree(nint handle);
}
