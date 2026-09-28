// The Rust engine's key agreement on Windows: .NET's ECDiffieHellman, which is CNG (BCrypt) there, behind the
// RipcordEcdhBackend table (docs/engine-plan.md, "Dependencies"). Phase 4 moves it into
// Ripcord.Protocol.Halyard.Native unchanged; it lives in the harness until then, so the harness's run of
// session-crypto.kat through it is the check the plan requires before a backend is used on its platform.
// On macOS and Linux the same code runs on .NET's own backends there, which is a useful cross-check but
// not the one that counts: CNG is only exercised on the Windows legs.
//
// The contract is ripcord_proto::crypto::ecdh's: a public key is the uncompressed SEC1 point, a shared
// secret is the X coordinate at the curve's full width, a peer point is validated on the curve before our
// scalar is used, and failure writes nothing and returns false.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Ripcord.Engine.Native;

static unsafe class PlatformEcdh
{
    // ripcord.h's RIPCORD_CURVE_*.
    const uint CurveP256 = 1;
    const uint CurveP521 = 2;

    public static RipcordEcdhBackend Table => new() { public_key = &PublicKey, shared_secret = &SharedSecret };

    static (ECCurve Curve, int Width)? For(uint curve) => curve switch
    {
        CurveP256 => (ECCurve.NamedCurves.nistP256, 32),
        CurveP521 => (ECCurve.NamedCurves.nistP521, 66),
        _ => null,
    };

    /// <summary>The key for a private scalar. .NET derives Q from D when Q is absent.</summary>
    static ECDiffieHellman? FromPrivate(ECCurve curve, int width, ReadOnlySpan<byte> d)
    {
        if (d.Length != width)
        {
            return null;
        }
        var key = ECDiffieHellman.Create();
        try
        {
            key.ImportParameters(new ECParameters { Curve = curve, D = d.ToArray() });
            return key;
        }
        catch (CryptographicException)
        {
            key.Dispose();
            return null;       // zero, or at or above the group order
        }
    }

    static bool Write(ReadOnlySpan<byte> value, byte* output, nuint capacity, nuint* length)
    {
        if ((nuint)value.Length > capacity || output == null || length == null)
        {
            return false;
        }
        value.CopyTo(new Span<byte>(output, value.Length));
        *length = (nuint)value.Length;
        return true;
    }

    static byte[] Pad(byte[] coordinate, int width)
    {
        if (coordinate.Length == width)
        {
            return coordinate;
        }
        var padded = new byte[width];
        coordinate.CopyTo(padded, width - coordinate.Length);
        return padded;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static bool PublicKey(void* user, uint curve, byte* privateKey, nuint privateLength, byte* output, nuint capacity, nuint* length)
    {
        try
        {
            if (For(curve) is not var (named, width) || privateKey == null)
            {
                return false;
            }
            using var key = FromPrivate(named, width, new ReadOnlySpan<byte>(privateKey, (int)privateLength));
            if (key is null)
            {
                return false;
            }
            ECParameters p = key.ExportParameters(false);
            byte[] point = [0x04, .. Pad(p.Q.X!, width), .. Pad(p.Q.Y!, width)];
            return Write(point, output, capacity, length);
        }
        catch (Exception)
        {
            return false;      // never unwind into the engine
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    static bool SharedSecret(void* user, uint curve, byte* privateKey, nuint privateLength, byte* peer, nuint peerLength,
                             byte* output, nuint capacity, nuint* length)
    {
        try
        {
            if (For(curve) is not var (named, width) || privateKey == null || peer == null)
            {
                return false;
            }
            var point = new ReadOnlySpan<byte>(peer, (int)peerLength);
            if (point.Length != 1 + 2 * width || point[0] != 0x04)
            {
                return false;  // uncompressed, on this curve's width, and nothing else
            }
            using var ours = FromPrivate(named, width, new ReadOnlySpan<byte>(privateKey, (int)privateLength));
            if (ours is null)
            {
                return false;
            }
            using var theirs = ECDiffieHellman.Create();
            // Importing validates the point on the curve before our scalar touches it.
            theirs.ImportParameters(new ECParameters
            {
                Curve = named,
                Q = new ECPoint { X = point.Slice(1, width).ToArray(), Y = point.Slice(1 + width, width).ToArray() },
            });
            byte[] secret = ours.DeriveRawSecretAgreement(theirs.PublicKey);
            return Write(Pad(secret, width), output, capacity, length);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
