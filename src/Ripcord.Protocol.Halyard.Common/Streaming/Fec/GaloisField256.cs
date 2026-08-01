namespace Ripcord.Protocol.Halyard.Common.Streaming.Fec;

/// <summary>
/// Arithmetic over GF(2^8) with the primitive polynomial x^8+x^4+x^3+x^2+1 (0x11d) — the field the console's
/// video FEC is defined over. 0x11d is the standard GF(256) polynomial used by Reed-Solomon coding and AES,
/// so it is the expected choice. Multiplication/division go through exp/log tables, built here.
///
/// <para><b>Provisional.</b> The polynomial is not confirmed against the console: the console's field table
/// is generated at runtime rather than stored, so pinning it needs a dynamic capture of that table. The table
/// build and operations below are our own.</para>
/// </summary>
internal static class GaloisField256
{
    private const int PrimitivePolynomial = 0x11d;

    private static readonly byte[] Exp = new byte[512]; // exp[i] = generator^i, doubled so a+b (<510) needs no mod
    private static readonly byte[] Log = new byte[256];  // log[exp[i]] = i; log[0] unused

    static GaloisField256()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if ((x & 0x100) != 0)
            {
                x ^= PrimitivePolynomial;
            }
        }

        // Duplicate the cycle so exp[i] is valid for i in [0, 510) — lets Multiply skip a modulo.
        for (int i = 255; i < 512; i++)
        {
            Exp[i] = Exp[i - 255];
        }
    }

    public static byte Multiply(byte a, byte b)
    {
        if (a == 0 || b == 0)
        {
            return 0;
        }

        return Exp[Log[a] + Log[b]];
    }

    public static byte Divide(byte a, byte b)
    {
        if (b == 0)
        {
            throw new DivideByZeroException("GF(256) division by zero.");
        }

        if (a == 0)
        {
            return 0;
        }

        return Exp[Log[a] - Log[b] + 255];
    }

    /// <summary>Multiplicative inverse: 1/a. a must be non-zero.</summary>
    public static byte Inverse(byte a) => Divide(1, a);
}
