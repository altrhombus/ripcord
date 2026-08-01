namespace Ripcord.Protocol.Halyard.Common.Streaming.Fec;

/// <summary>
/// Systematic Cauchy Reed-Solomon erasure coding over <see cref="GaloisField256"/>, matching the console's
/// video FEC:
/// <c>k</c> source units followed by <c>m</c> parity units, recoverable as long as any <c>k</c> of the
/// <c>k+m</c> units survive. The coding matrix entry is <c>1 / (i XOR (m+j))</c> in GF(2^8).
///
/// <para>Units live in one contiguous <c>frameBuf</c>, unit <c>u</c> at <c>u*stride</c>, each logically
/// <c>unitSize</c> bytes (any tail up to <c>stride</c> is ignored). All coding is per-byte and independent
/// across byte positions.
///
/// <para><b>Provisional.</b> The linear algebra (Gauss-Jordan inverse, encode/decode) is ours, but the
/// <em>choice</em> of a Cauchy matrix over the alternatives is an assumption our own analysis has not settled
/// against the console. If FEC recovery ever misbehaves, this is a prime suspect — along with the per-unit
/// stride in the demuxer. See the roadmap's provisional-values list.</para>
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
                matrix[i * k + j] = GaloisField256.Inverse((byte)(i ^ (m + j)));
            }
        }

        return matrix;
    }

    /// <summary>
    /// Encode: compute the <c>m</c> parity units (indices k..k+m-1) from the <c>k</c> source units in place.
    /// Used to build fixtures/tests; the client only ever decodes.
    /// </summary>
    public static void Encode(byte[] frameBuf, int unitSize, int stride, int k, int m)
    {
        byte[] matrix = BuildCodingMatrix(k, m);
        for (int i = 0; i < m; i++)
        {
            int parityOffset = (k + i) * stride;
            Array.Clear(frameBuf, parityOffset, unitSize);
            for (int j = 0; j < k; j++)
            {
                byte coeff = matrix[i * k + j];
                if (coeff == 0)
                {
                    continue;
                }

                int srcOffset = j * stride;
                for (int t = 0; t < unitSize; t++)
                {
                    frameBuf[parityOffset + t] ^= GaloisField256.Multiply(coeff, frameBuf[srcOffset + t]);
                }
            }
        }
    }

    /// <summary>
    /// Reconstruct the erased SOURCE units (index &lt; k with <c>present[index] == false</c>) in place, using
    /// the surviving source + parity units. Returns false if fewer than <c>k</c> units survive (unrecoverable)
    /// or the selected system is singular. Present source units are left untouched.
    /// </summary>
    public static bool Decode(byte[] frameBuf, int unitSize, int stride, int k, int m, bool[] present)
    {
        int total = k + m;
        // Choose any k surviving units; each contributes one equation of the linear system.
        var chosen = new List<int>(k);
        for (int u = 0; u < total && chosen.Count < k; u++)
        {
            if (present[u])
            {
                chosen.Add(u);
            }
        }

        if (chosen.Count < k)
        {
            return false; // too many erasures
        }

        // Any erased source units to reconstruct? (If every source survived there is nothing to do.)
        bool anyErasedSource = false;
        for (int e = 0; e < k; e++)
        {
            if (!present[e])
            {
                anyErasedSource = true;
                break;
            }
        }

        if (!anyErasedSource)
        {
            return true;
        }

        byte[] matrix = BuildCodingMatrix(k, m);

        // Build A (k×k): row r = the generator row of chosen unit r (identity row for a source unit, the
        // coding-matrix row for a parity unit). A · data = chosenValues, so data = A⁻¹ · chosenValues.
        var a = new byte[k * k];
        for (int r = 0; r < k; r++)
        {
            int u = chosen[r];
            if (u < k)
            {
                a[r * k + u] = 1;
            }
            else
            {
                int codingRow = u - k;
                for (int j = 0; j < k; j++)
                {
                    a[r * k + j] = matrix[codingRow * k + j];
                }
            }
        }

        byte[]? inv = Invert(a, k);
        if (inv is null)
        {
            return false;
        }

        // For each erased source unit e: data[e] = Σ_r inv[e][r] · chosenUnit[r] (per byte).
        for (int e = 0; e < k; e++)
        {
            if (present[e])
            {
                continue;
            }

            int destOffset = e * stride;
            Array.Clear(frameBuf, destOffset, unitSize);
            for (int r = 0; r < k; r++)
            {
                byte coeff = inv[e * k + r];
                if (coeff == 0)
                {
                    continue;
                }

                int srcOffset = chosen[r] * stride;
                for (int t = 0; t < unitSize; t++)
                {
                    frameBuf[destOffset + t] ^= GaloisField256.Multiply(coeff, frameBuf[srcOffset + t]);
                }
            }
        }

        return true;
    }

    /// <summary>Gauss-Jordan inverse of an n×n GF(2^8) matrix (row-major); null if singular.</summary>
    private static byte[]? Invert(byte[] source, int n)
    {
        var work = (byte[])source.Clone();
        var inv = new byte[n * n];
        for (int i = 0; i < n; i++)
        {
            inv[i * n + i] = 1;
        }

        for (int col = 0; col < n; col++)
        {
            // Find a pivot row at/below `col` with a non-zero entry in this column.
            int pivot = -1;
            for (int r = col; r < n; r++)
            {
                if (work[r * n + col] != 0)
                {
                    pivot = r;
                    break;
                }
            }

            if (pivot < 0)
            {
                return null; // singular
            }

            if (pivot != col)
            {
                SwapRows(work, n, pivot, col);
                SwapRows(inv, n, pivot, col);
            }

            // Normalize the pivot row so work[col][col] == 1.
            byte invPivot = GaloisField256.Inverse(work[col * n + col]);
            for (int j = 0; j < n; j++)
            {
                work[col * n + j] = GaloisField256.Multiply(work[col * n + j], invPivot);
                inv[col * n + j] = GaloisField256.Multiply(inv[col * n + j], invPivot);
            }

            // Eliminate this column from every other row.
            for (int r = 0; r < n; r++)
            {
                if (r == col)
                {
                    continue;
                }

                byte factor = work[r * n + col];
                if (factor == 0)
                {
                    continue;
                }

                for (int j = 0; j < n; j++)
                {
                    work[r * n + j] ^= GaloisField256.Multiply(factor, work[col * n + j]);
                    inv[r * n + j] ^= GaloisField256.Multiply(factor, inv[col * n + j]);
                }
            }
        }

        return inv;
    }

    private static void SwapRows(byte[] matrix, int n, int r0, int r1)
    {
        for (int j = 0; j < n; j++)
        {
            (matrix[r0 * n + j], matrix[r1 * n + j]) = (matrix[r1 * n + j], matrix[r0 * n + j]);
        }
    }
}
