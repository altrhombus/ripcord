using Ripcord.Core.Platform;

namespace Ripcord.Protocol.Halyard.Session;

/// <summary>
/// The client's device identity for the RP-Did control field (spec §2.1): a stable 16-byte machine id.
///
/// <para>
/// This used to read the Windows registry directly, which put a <c>Microsoft.Win32.Registry</c> dependency and
/// a Windows-only code path into an otherwise platform-neutral protocol assembly. The host detail now lives
/// behind <see cref="IDeviceIdentity"/> in Core, so this is a thin protocol-facing wrapper.
/// </para>
/// </summary>
public static class HalyardDeviceIdentity
{
    private static readonly IDeviceIdentity Default = new DefaultDeviceIdentity();

    /// <summary>The current machine's device id (16 bytes), or empty if unavailable.</summary>
    public static ReadOnlyMemory<byte> Current() => Default.StableDeviceId;

    /// <summary>The device id from an explicit provider (tests, or a host that supplies its own).</summary>
    public static ReadOnlyMemory<byte> From(IDeviceIdentity identity) => identity.StableDeviceId;

    /// <summary>Strip dashes from a GUID string and hex-decode to 16 bytes. Returns false if malformed.</summary>
    public static bool TryParseMachineGuid(string machineGuid, out byte[] bytes)
        => DefaultDeviceIdentity.TryParseMachineGuid(machineGuid, out bytes);
}
