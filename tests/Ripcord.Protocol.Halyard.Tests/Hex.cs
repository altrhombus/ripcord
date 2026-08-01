namespace Ripcord.Protocol.Halyard.Tests;

internal static class Hex
{
    public static byte[] Bytes(string hex) => Convert.FromHexString(hex.Replace(" ", "").Replace("\n", ""));

    public static string String(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
