using System.Net;
using Ripcord.Core.Consoles;
using Ripcord.Core.Security;
using Ripcord.Presentation.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Discovery;
using Ripcord.Protocol.Halyard.Session;

namespace Ripcord.Presentation.Halyard.Sessions;

/// <summary>
/// Waking a PlayStation console, behind the portable seam.
///
/// <para>
/// This is where the family-specific detail belongs and stays: a PS4 wakes on 987 with protocol 00020020
/// (WAKEUP from 987, SRCH from an ephemeral port), a PS5 on 9302 with 00030010 (both from 9303). The discovery
/// profile carries those, so one composition drives either — and the session surface above never learns that
/// any of it exists.
/// </para>
///
/// <para>
/// It is also the only caller of <c>PairedConsole.ToPairingRecord</c>, the extension that keeps
/// <c>Ripcord.Core</c> from having to know what a Halyard pairing record is.
/// </para>
/// </summary>
public sealed class HalyardConsoleWakeCoordinator : IConsoleWakeCoordinator
{
    private readonly ICredentialProtector _protector;
    private readonly TimeSpan _probeTimeout;

    public HalyardConsoleWakeCoordinator(ICredentialProtector? protector = null, TimeSpan? probeTimeout = null)
    {
        _protector = protector ?? CredentialProtection.ForCurrentPlatform();
        _probeTimeout = probeTimeout ?? TimeSpan.FromSeconds(1);
    }

    public async Task<ConsoleWakeOutcome> EnsureAwakeAsync(
        PairedConsole console,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(console);

        if (!IPAddress.TryParse(console.Host, out IPAddress? address))
        {
            return ConsoleWakeOutcome.Unknown;
        }

        HalyardPairingRecord? record = console.ToPairingRecord(_protector);
        if (record is null)
        {
            // No usable pairing record means no wake credential. Not a failure here: connect will fail with its
            // own, far clearer "not paired / bad credential" message than anything this layer could invent.
            return ConsoleWakeOutcome.Unknown;
        }

        var profile = HalyardDiscoveryProfile.ForPlatformName(console.Platform);
        var search = new HalyardSearchClient(profile);
        var wake = new HalyardWakeClient(profile);

        var coordinator = new HalyardWakeCoordinator(
            // Probe from the family's wake-search source port so the exchange matches the vendor's. This path is
            // sequential, so there is no contention for a fixed port the way the console list has.
            probeAwake: async ct =>
                (await search.ProbeAsync(address, _probeTimeout, ct, profile.WakeSearchSourcePort)
                    .ConfigureAwait(false))?.IsAwake,
            sendWake: ct => wake.WakeAsync(address, record, ct));

        WakeOutcome outcome = await coordinator
            .EnsureAwakeAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        return outcome switch
        {
            WakeOutcome.AlreadyAwake => ConsoleWakeOutcome.AlreadyAwake,
            WakeOutcome.Woke => ConsoleWakeOutcome.Woken,
            WakeOutcome.TimedOut => ConsoleWakeOutcome.TimedOut,

            // NotFound means the console did not answer discovery at all — which is not proof it is asleep. It
            // may be awake but slow, or reachable only at its stored address, so the caller should still try.
            _ => ConsoleWakeOutcome.Unknown,
        };
    }
}
