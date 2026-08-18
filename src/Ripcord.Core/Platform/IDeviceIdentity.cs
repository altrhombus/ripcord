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
public sealed partial class DefaultDeviceIdentity : IDeviceIdentity
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

    // Reading MachineGuid needs the registry, and this assembly is neutral net10.0 rather than
    // net10.0-windows, so Microsoft.Win32.Registry is not in its reference closure.
    //
    // This USED to reflect for those types instead, and the reflection silently returned nothing in exactly
    // the hosts that are also neutral net10.0 -- the console harness among them -- while working fine inside
    // the WinUI app, which does reference the Windows assemblies. The result was a device id that was correct
    // on one host and empty on another, presenting as "this machine did not supply a stable device id" from a
    // tool that was running on a perfectly ordinary Windows machine.
    //
    // Calling the API directly is both simpler and host-independent: identical value in every host, no
    // assembly-resolution guesswork, and it drops the IL2075 trim warning the reflection carried (the last
    // non-JSON one in the tree).

    private const nint HkeyLocalMachine = unchecked((nint)(int)0x80000002);

    /// <summary>Restrict the lookup to a string value, so a tampered key of another type cannot be misread.</summary>
    private const uint RestrictToString = 0x00000002;

    private const string CryptographyKey = @"SOFTWARE\Microsoft\Cryptography";
    private const string MachineGuidValue = "MachineGuid";

    [System.Runtime.InteropServices.LibraryImport(
        "advapi32.dll",
        EntryPoint = "RegGetValueW",
        StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
    private static partial int RegGetValue(
        nint key,
        string subKey,
        string value,
        uint flags,
        out uint type,
        byte[]? data,
        ref uint dataSize);

    /// <summary>
    /// Reads <c>HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid</c>. Returns null rather than throwing on any
    /// failure — an absent device id is a protocol-level concern for the caller to report, not a crash here.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? ReadWindowsMachineGuid()
    {
        // Size query first: the value is a fixed-length GUID string in practice, but asking rather than
        // assuming costs one call and cannot be wrong.
        uint size = 0;
        if (RegGetValue(HkeyLocalMachine, CryptographyKey, MachineGuidValue, RestrictToString, out _, null, ref size) != 0
            || size == 0)
        {
            return null;
        }

        byte[] buffer = new byte[size];
        if (RegGetValue(HkeyLocalMachine, CryptographyKey, MachineGuidValue, RestrictToString, out _, buffer, ref size) != 0)
        {
            return null;
        }

        // size comes back as bytes including the terminating NUL, which TrimEnd removes.
        return System.Text.Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0');
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
