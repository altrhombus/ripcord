using System.Net;
using System.Text;
using Ripcord.Core.Net.Udp;

namespace Ripcord.Protocol.Halyard.Common.Discovery;

/// <summary>A console's reply to the SRCH probe (docs/protocol/ps5-local-discovery.md).</summary>
public sealed record HalyardSearchResult(
    string HostId,
    string HostType,
    string HostName,
    int RequestPort,
    string SystemVersion,
    IPAddress Address,
    bool IsAwake);

/// <summary>
/// The remote-play LAN discovery probe: a lightweight <c>SRCH * HTTP/1.1</c> broadcast on UDP 9302,
/// to which a console replies with its host-id, name, type, request-port and firmware version. Fully
/// specified from our own capture (docs/protocol/ps5-local-discovery.md). The probe/protocol-version
/// strings are required on-wire tokens.
/// </summary>
public sealed class HalyardSearchClient
{
    public const int DiscoveryPort = 9302;
    public const string ProtocolVersion = "00030010";

    private static readonly byte[] SearchProbe = Encoding.ASCII.GetBytes(
        $"SRCH * HTTP/1.1\r\ndevice-discovery-protocol-version:{ProtocolVersion}\r\n\r\n");

    /// <summary>Broadcast a SRCH probe and collect console replies for <paramref name="window"/>.</summary>
    public async Task<IReadOnlyList<HalyardSearchResult>> SearchAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        using var udp = new UdpChannel(localPort: 0, enableBroadcast: true);
        await udp.SendBroadcastAsync(SearchProbe, DiscoveryPort, cancellationToken).ConfigureAwait(false);

        var results = new Dictionary<string, HalyardSearchResult>(StringComparer.OrdinalIgnoreCase);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(window);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                var received = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                if (TryParse(received.Buffer, received.RemoteEndPoint.Address, out HalyardSearchResult? result))
                {
                    results[result!.HostId] = result;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // window elapsed
        }

        return [.. results.Values];
    }

    internal static bool TryParse(ReadOnlySpan<byte> datagram, IPAddress source, out HalyardSearchResult? result)
    {
        result = null;
        string text = Encoding.ASCII.GetString(datagram);
        string[] lines = text.Split('\n');
        if (lines.Length == 0 || !lines[0].StartsWith("HTTP/1.1", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Status line distinguishes power state: 200 = awake, 620 = standby.
        string[] statusParts = lines[0].Split(' ', 3);
        bool isAwake = statusParts.Length >= 2 && statusParts[1] == "200";

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines)
        {
            int colon = line.IndexOf(':');
            if (colon > 0)
            {
                fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        if (!fields.TryGetValue("host-id", out var hostId))
        {
            return false;
        }

        _ = int.TryParse(fields.GetValueOrDefault("host-request-port"), out int requestPort);
        result = new HalyardSearchResult(
            hostId,
            fields.GetValueOrDefault("host-type", "PS5"),
            fields.GetValueOrDefault("host-name", string.Empty),
            requestPort,
            fields.GetValueOrDefault("system-version", string.Empty),
            source,
            isAwake);
        return true;
    }
}
