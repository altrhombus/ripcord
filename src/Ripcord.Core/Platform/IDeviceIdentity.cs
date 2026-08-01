namespace Ripcord.Core.Platform;

/// <summary>
/// A stable 16-byte identifier for this client device. The console protocol needs one to identify us across
/// sessions; the point of the seam is that "how do I identify this machine" is a platform question, and having
/// a protocol project answer it directly is what forced a Windows registry dependency into otherwise
/// platform-neutral code.
/// </summary>
public interface IDeviceIdentity
{
    /// <summary>The device id (16 bytes), or empty when the host cannot supply one.</summary>
    ReadOnlyMemory<byte> StableDeviceId { get; }
}

/// <summary>
/// Reads the host's machine identifier and reduces it to 16 bytes. Conveniently, every platform's native
/// identifier is a 32-hex-character GUID/UUID, so one parse serves all of them:
/// <list type="bullet">
///   <item><description>Windows — <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c>.</description></item>
///   <item><description>Linux — <c>/etc/machine-id</c> (or the D-Bus fallback), already bare hex.</description></item>
///   <item><description>macOS — no stable file, so callers should inject one.</description></item>
/// </list>
/// Degrades to empty rather than throwing: an absent device id is a protocol-level concern, not a crash.
/// </summary>
public sealed class DefaultDeviceIdentity : IDeviceIdentity
{
    private readonly Lazy<ReadOnlyMemory<byte>> _id = new(Read);

    public ReadOnlyMemory<byte> StableDeviceId => _id.Value;

    private static ReadOnlyMemory<byte> Read()
    {
        try
        {
            string? raw = OperatingSystem.IsWindows() ? ReadWindowsMachineGuid() : ReadUnixMachineId();
            return raw is not null && TryParseMachineGuid(raw, out byte[] bytes) ? bytes : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return default;
        }
    }

    /// <summary>
    /// Reads MachineGuid without taking a dependency on <c>Microsoft.Win32.Registry</c>, so this stays usable
    /// from a neutral (<c>net10.0</c>) assembly. Uses reflection over the registry types, which are present in
    /// the Windows runtime pack — absent or failing, we return null and the caller degrades.
    /// </summary>
    private static string? ReadWindowsMachineGuid()
    {
        Type? registry = Type.GetType("Microsoft.Win32.Registry, Microsoft.Win32.Registry")
            ?? Type.GetType("Microsoft.Win32.Registry, System.Private.CoreLib")
            ?? Type.GetType("Microsoft.Win32.Registry");
        if (registry?.GetProperty("LocalMachine")?.GetValue(null) is not { } localMachine)
        {
            return null;
        }

        object? key = localMachine.GetType()
            .GetMethod("OpenSubKey", [typeof(string)])
            ?.Invoke(localMachine, [@"SOFTWARE\Microsoft\Cryptography"]);
        if (key is null)
        {
            return null;
        }

        using (key as IDisposable)
        {
            return key.GetType()
                .GetMethod("GetValue", [typeof(string)])
                ?.Invoke(key, ["MachineGuid"]) as string;
        }
    }

    /// <summary>systemd's machine-id, with the historical D-Bus location as a fallback.</summary>
    private static string? ReadUnixMachineId()
    {
        foreach (string path in (string[])["/etc/machine-id", "/var/lib/dbus/machine-id"])
        {
            if (File.Exists(path))
            {
                string value = File.ReadAllText(path).Trim();
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return null;
    }

    /// <summary>Strip dashes from a GUID/UUID string and hex-decode to 16 bytes. False if malformed.</summary>
    public static bool TryParseMachineGuid(string machineGuid, out byte[] bytes)
    {
        bytes = [];
        string hex = machineGuid.Replace("-", string.Empty).Trim();
        if (hex.Length != 32)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(hex);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>A fixed device id, for tests and for hosts that supply their own.</summary>
public sealed class StaticDeviceIdentity(ReadOnlyMemory<byte> id) : IDeviceIdentity
{
    public ReadOnlyMemory<byte> StableDeviceId { get; } = id;
}
