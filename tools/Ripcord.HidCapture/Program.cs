using System.Runtime.Versioning;
using Ripcord.HidCapture;

// Standalone HID input-report capture tool for reverse-engineering controller report layouts (DualSense
// first). Lists HID devices and dumps the raw input reports of a chosen one to a file, in the same
// [u16 little-endian length][report bytes] record format as the Opus dump, preceded by a one-line JSON
// header. Feed the resulting file to the managed report-parser tests.
//
//   Ripcord.HidCapture --list
//   Ripcord.HidCapture [--device N] [--out file.bin]
//
// Publish as a self-contained single exe to run on machines with no dev environment (e.g. the ROG Ally X):
//   dotnet publish tools/Ripcord.HidCapture -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Ripcord.HidCapture runs on Windows only (SetupAPI/hid.dll).");
    return 1;
}

return Run(args);

[SupportedOSPlatform("windows")]
static int Run(string[] args)
{
    int deviceIndex = -1;
    string? outPath = null;
    bool listOnly = false;

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--list":
                listOnly = true;
                break;
            case "--device" when i + 1 < args.Length:
                int.TryParse(args[++i], out deviceIndex);
                break;
            case "--out" when i + 1 < args.Length:
                outPath = args[++i];
                break;
            default:
                Console.Error.WriteLine($"Unknown argument: {args[i]}");
                return 2;
        }
    }

    List<HidNative.HidDeviceInfo> devices = HidNative.Enumerate();
    if (devices.Count == 0)
    {
        Console.Error.WriteLine("No HID devices found.");
        return 1;
    }

    Console.WriteLine($"{"#",3}  {"VID:PID",-9}  {"usage",-11}  {"in",-4}  product");
    for (int i = 0; i < devices.Count; i++)
    {
        HidNative.HidDeviceInfo d = devices[i];
        Console.WriteLine($"{i,3}  {d.VendorId:X4}:{d.ProductId:X4}  {d.UsagePage:X2}/{d.Usage:X2}       {d.InputReportByteLength,4}  {d.Product}");
    }

    if (listOnly)
    {
        return 0;
    }

    if (deviceIndex < 0)
    {
        Console.Write("\nDevice index to capture (or Enter to quit): ");
        string? line = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(line) || !int.TryParse(line, out deviceIndex))
        {
            return 0;
        }
    }

    if (deviceIndex < 0 || deviceIndex >= devices.Count)
    {
        Console.Error.WriteLine($"Device index {deviceIndex} out of range.");
        return 2;
    }

    HidNative.HidDeviceInfo dev = devices[deviceIndex];
    outPath ??= $"hidcapture-{dev.VendorId:X4}-{dev.ProductId:X4}.bin";

    if (!HidNative.TryOpen(dev.Path, out var handle))
    {
        Console.Error.WriteLine($"Could not open device {deviceIndex} ({dev.Product}). It may be held exclusively (close Steam / other controller apps).");
        return 1;
    }

    Console.WriteLine($"\nCapturing {dev.VendorId:X4}:{dev.ProductId:X4} \"{dev.Product}\" -> {outPath}");
    Console.WriteLine("Press every button, move both sticks/triggers, use the touchpad/gyro. Ctrl+C to stop.\n");

    using var output = new BinaryWriter(File.Create(outPath));
    string header = $"{{\"magic\":\"RHID\",\"version\":1,\"vid\":\"{dev.VendorId:X4}\",\"pid\":\"{dev.ProductId:X4}\"," +
        $"\"product\":{System.Text.Json.JsonSerializer.Serialize(dev.Product)},\"usagePage\":{dev.UsagePage}," +
        $"\"usage\":{dev.Usage},\"inputReportLen\":{dev.InputReportByteLength}}}\n";
    output.Write(System.Text.Encoding.UTF8.GetBytes(header));

    long count = 0;
    bool stop = false;
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;   // let us flush cleanly instead of hard-killing
        stop = true;
        handle.Dispose();  // unblock the pending ReadFile
    };

    // Report buffer sized to the reported input length (fall back to 512 for devices that don't report one).
    var buffer = new byte[dev.InputReportByteLength > 0 ? dev.InputReportByteLength : 512];
    while (!stop)
    {
        int read = HidNative.ReadReport(handle, buffer);
        if (read <= 0)
        {
            break; // handle closed (Ctrl+C) or read error
        }

        output.Write((ushort)read);
        output.Write(buffer, 0, read);
        count++;

        if (count == 1)
        {
            Console.WriteLine($"first report ({read} bytes): {Convert.ToHexString(buffer.AsSpan(0, Math.Min(read, 24)))}...");
        }

        if (count % 250 == 0)
        {
            Console.Write($"\rreports captured: {count}   ");
        }
    }

    output.Flush();
    Console.WriteLine($"\nDone. {count} reports written to {outPath}.");
    return 0;
}
