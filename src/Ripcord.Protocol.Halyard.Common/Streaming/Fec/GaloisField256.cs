namespace Ripcord.Protocol.Halyard.Common.Streaming.Fec;

/// <summary>
/// Arithmetic over GF(2^8) with the primitive polynomial x^8+x^4+x^3+x^2+1 (0x11d) — the field the console's
/// video FEC is defined over. Multiplication/division go through exp/log tables, built here.
///
/// <para><b>Confirmed [V] 2026-08-02</b>, two independent ways, and it is a <b>fixed compile-time constant —
/// not negotiated, not per-session, not per-console</b>. Statically: the vendor's table builder
/// <c>FUN_1015eed0</c> is the textbook exp/log generator loop, and at <c>0x1015ef5d</c> it does
/// <c>xor ecx, 0x1d</c> — the low-byte form of 0x11d, a literal immediate in the instruction stream. That
/// function is a <c>thiscall</c> taking no arguments, reading no globals, and guarded to build once; nothing
/// session-dependent can reach it. Dynamically: a breakpoint at <c>0x101036db</c> in the coding-matrix
/// builder dumped the live table, which reproduces 0x11d exactly (no other primitive polynomial matches more
/// than one entry).</para>
///
/// <para>"Runtime-generated" therefore means <i>computed at init from a hardcoded constant</i> — the same
/// thing the static constructor below does — and not <i>variable</i>. The tables are built once and cached
/// for the process lifetime on both sides.</para>
///
/// <para>The vendor's field object holds four tables: log (0x100) at +0, exp (0x300, pointer biased by +0xFF
/// so division's negative indices work) at +4, a 64 KB <b>divide</b> table at +8, and a 64 KB
/// <b>multiply</b> table at +0xc, both indexed <c>[a &lt;&lt; 8 | b]</c>. The <c>table[value | 0x100]</c>
/// lookup the matrix construction uses is simply row <c>a = 1</c> of the divide table, i.e. <c>1 / value</c>
/// — which is why it acts as an inverse table. Division by zero stores a 0xff sentinel where we throw
/// instead; nothing ever indexes it (see <see cref="CauchyReedSolomon"/>). The table build and the operations
/// below are our own.</para>
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
