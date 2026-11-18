using Ripcord.Core.Platform;

namespace Ripcord.Protocol.Halyard.Session;

/// <summary>
/// The client's device identity for the RP-Did control field (spec §2.1): a stable 16-byte machine id.
///
/// <para>
/// A thin protocol-facing wrapper. The host detail lives behind <see cref="IDeviceIdentity"/> in Core, which
/// keeps the <c>Microsoft.Win32.Registry</c> dependency and its Windows-only code path out of this
/// platform-neutral protocol assembly.
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
