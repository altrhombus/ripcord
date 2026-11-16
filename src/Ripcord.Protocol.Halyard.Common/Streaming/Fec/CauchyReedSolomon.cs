namespace Ripcord.Protocol.Halyard.Common.Streaming.Fec;

/// <summary>
/// Systematic Cauchy Reed-Solomon erasure coding over <see cref="GaloisField256"/>, as the console codes its
/// video (spec §6.2): <c>k</c> source units followed by <c>m</c> parity units, parity unit <c>i</c> being
/// <c>Σ_j C[i][j] · source_j</c> with <c>C[i][j] = 1 / (i XOR (m + j))</c>.
///
/// <para>Units are laid out the way the console lays them out: one contiguous span, unit <c>u</c> at
/// <c>u * codedLength</c>, every unit exactly <c>codedLength</c> bytes. Coding is per byte position.</para>
///
/// <para>Recovery works on the erased source units only. With <c>e</c> source units missing, take any <c>e</c>
/// parity units that arrived, remove the arrived source units' share from each (what is left depends only on
/// the missing ones), and solve the <c>e × e</c> system given by those parity rows and the missing columns of
/// <c>C</c>. Every square submatrix of a Cauchy matrix is invertible, so the system always has a solution
/// when <c>e</c> parity units arrived.</para>
///
/// <para>Provenance: the matrix form is <b>[C]</b> from our own decompilation (the console builds each element
/// as <c>table[((m + j) XOR i) | 0x100]</c>, the field-inverse row), the field polynomial <b>[V]</b> 0x11d from a
/// runtime dump of the vendor client's own table, and the coded length <b>[C][W]</b>. The linear algebra is
/// ours. <c>i &lt; m &lt;= m + j</c> for every valid <c>i, j</c>, so <c>i XOR (m + j)</c> is never zero and
/// the inverse is always defined.</para>
/// </summary>
internal static class CauchyReedSolomon
{
    /// <summary>The m×k coding matrix, row-major: <c>matrix[i*k + j] = 1 / (i XOR (m+j))</c>.</summary>
    public static byte[] BuildCodingMatrix(int k, int m)
    {
        var matrix = new byte[m * k];
        for (int i = 0; i < m; i++)
        {
            for (int j = 0; j < k; j++)
            {
                matrix[i * k + j] = Coefficient(i, j, m);
            }
        }

        return matrix;
    }

    private static byte Coefficient(int parityRow, int sourceColumn, int parityCount)
        => GaloisField256.Inverse((byte)(parityRow ^ (parityCount + sourceColumn)));

    /// <summary>
    /// Compute the <paramref name="parityCount"/> parity units from the source units, in place. For fixtures and
    /// tests; the client only ever recovers.
    /// </summary>
    public static void Encode(Span<byte> units, int codedLength, int sourceCount, int parityCount)
    {
        for (int i = 0; i < parityCount; i++)
        {
            Span<byte> parity = units.Slice((sourceCount + i) * codedLength, codedLength);
            parity.Clear();
            for (int j = 0; j < sourceCount; j++)
            {
                MultiplyAdd(parity, Coefficient(i, j, parityCount), units.Slice(j * codedLength, codedLength));
            }
        }
    }

    /// <summary>
    /// Rebuild the source units that did not arrive (<c>arrived[u] == false</c>, <c>u &lt; sourceCount</c>) in
    /// place. Returns false when fewer parity units arrived than source units are missing; arrived units are
    /// never written.
    /// </summary>
    public static bool RecoverSource(
        Span<byte> units, int codedLength, int sourceCount, int parityCount, ReadOnlySpan<bool> arrived)
    {
        var missing = new List<int>();
        for (int j = 0; j < sourceCount; j++)
        {
            if (!arrived[j])
                missing.Add(j);
        }

        if (missing.Count == 0)
            return true;

        var rows = new List<int>(missing.Count);
        for (int i = 0; i < parityCount && rows.Count < missing.Count; i++)
        {
            if (arrived[sourceCount + i])
                rows.Add(i);
        }

        if (rows.Count < missing.Count)
            return false;

        int e = missing.Count;

        // What each chosen parity unit owes to the missing source units alone: the parity unit minus the share
        // of every source unit that did arrive.
        byte[] remainder = new byte[e * codedLength];
        for (int r = 0; r < e; r++)
        {
            Span<byte> owed = remainder.AsSpan(r * codedLength, codedLength);
            units.Slice((sourceCount + rows[r]) * codedLength, codedLength).CopyTo(owed);
            for (int j = 0; j < sourceCount; j++)
            {
                if (arrived[j])
                    MultiplyAdd(owed, Coefficient(rows[r], j, parityCount), units.Slice(j * codedLength, codedLength));
            }
        }

        // remainder = S · missingUnits, where S[r][c] = C[rows[r]][missing[c]]. Invert S and apply it.
        var system = new byte[e * e];
        for (int r = 0; r < e; r++)
        {
            for (int c = 0; c < e; c++)
                system[r * e + c] = Coefficient(rows[r], missing[c], parityCount);
        }

        byte[]? inverse = Invert(system, e);
        if (inverse is null)
            return false; // unreachable for a Cauchy submatrix; kept so a bad table fails closed

        for (int c = 0; c < e; c++)
        {
            Span<byte> rebuilt = units.Slice(missing[c] * codedLength, codedLength);
            rebuilt.Clear();
            for (int r = 0; r < e; r++)
                MultiplyAdd(rebuilt, inverse[c * e + r], remainder.AsSpan(r * codedLength, codedLength));
        }

        return true;
    }

    /// <summary><c>destination ^= coefficient · source</c>, byte by byte.</summary>
    private static void MultiplyAdd(Span<byte> destination, byte coefficient, ReadOnlySpan<byte> source)
    {
        if (coefficient == 0)
            return;

        for (int t = 0; t < destination.Length; t++)
            destination[t] ^= GaloisField256.Multiply(coefficient, source[t]);
    }

    /// <summary>Gauss-Jordan inverse of an n×n GF(2^8) matrix (row-major); null if singular.</summary>
    private static byte[]? Invert(byte[] source, int n)
    {
        var work = (byte[])source.Clone();
        var inverse = new byte[n * n];
        for (int i = 0; i < n; i++)
            inverse[i * n + i] = 1;

        for (int column = 0; column < n; column++)
        {
            int pivot = column;
            while (pivot < n && work[pivot * n + column] == 0)
                pivot++;

            if (pivot == n)
                return null;

            if (pivot != column)
            {
                SwapRows(work, n, pivot, column);
                SwapRows(inverse, n, pivot, column);
            }

            byte scale = GaloisField256.Inverse(work[column * n + column]);
            for (int j = 0; j < n; j++)
            {
                work[column * n + j] = GaloisField256.Multiply(work[column * n + j], scale);
                inverse[column * n + j] = GaloisField256.Multiply(inverse[column * n + j], scale);
            }

            for (int r = 0; r < n; r++)
            {
                byte factor = work[r * n + column];
                if (r == column || factor == 0)
                    continue;

                for (int j = 0; j < n; j++)
                {
                    work[r * n + j] ^= GaloisField256.Multiply(factor, work[column * n + j]);
                    inverse[r * n + j] ^= GaloisField256.Multiply(factor, inverse[column * n + j]);
                }
            }
        }

        return inverse;
    }

    private static void SwapRows(byte[] matrix, int n, int a, int b)
    {
        for (int j = 0; j < n; j++)
            (matrix[a * n + j], matrix[b * n + j]) = (matrix[b * n + j], matrix[a * n + j]);
    }
}
