using System;
using System.IO;

namespace Ripcord.Media;

/// <summary>
/// <c>RIPCORD_DUMP_VIDEO=1</c>: write the first few seconds of the decrypted video stream, as received, to
/// <c>%LocalAppData%\Ripcord\state\video-dump-*.bin</c>. A developer switch, off unless set.
///
/// <para>
/// <b>Why.</b> What the console declares about its picture (range, primaries, matrix) is in the stream's own
/// parameter sets, and Windows' decoder did not pass it on: the panel read "BT.709 (assumed)" while Ripcord
/// clipped highlights the console's own screenshot did not (2026-10-02). Reading the declaration means reading
/// the stream. It holds no address or key, but it is the console's picture, and the console draws notifications
/// into that, with the signed-in account's online ID: keep a dump in the captures folder, never in a record.
/// </para>
///
/// <para>Called from the one thread that submits video, so it needs no lock.</para>
/// </summary>
internal static class VideoDump
{
    /// <summary>Enough for several keyframes and their parameter sets, without filling a disk.</summary>
    private const long Limit = 16 * 1024 * 1024;

    private static readonly bool Enabled = Environment.GetEnvironmentVariable("RIPCORD_DUMP_VIDEO") is "1";

    private static FileStream? _file;
    private static long _written;
    private static bool _done;

    public static void Append(ReadOnlySpan<byte> payload)
    {
        if (!Enabled || _done)
        {
            return;
        }

        try
        {
            if (_file is null)
            {
                // The app's own state folder, so a RIPCORD_DATA_DIR profile keeps its dumps with the rest of it.
                string dir = new Ripcord.Core.Platform.DefaultPlatformPaths().StateDirectory;
                _file = new FileStream(
                    Path.Combine(dir, $"video-dump-{DateTime.Now:yyyyMMdd-HHmmss}.bin"), FileMode.CreateNew);
            }

            _file.Write(payload);
            _file.Flush();   // so a dump cut short by a disconnect is still readable
            _written += payload.Length;

            if (_written >= Limit)
            {
                _done = true;
                _file.Dispose();
                _file = null;
            }
        }
        catch (Exception)
        {
            // A dump is never worth a stream: stop trying.
            _done = true;
        }
    }
}
