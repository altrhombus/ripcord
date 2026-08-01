using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Ripcord.Core.Net.Mdns;

/// <summary>A host observed on the LAN via mDNS: its <c>.local</c> name and IPv4 address.</summary>
public sealed record MdnsHost(string HostName, IPAddress Address);

/// <summary>
/// Minimal mDNS/DNS-SD browser. Sends a multicast query and collects the A records seen in replies
/// over a short window - enough to spot a console announcing itself under its <c>.local</c> name
/// (docs/protocol/ps5-network-architecture.md). A lightweight presence check; the richer,
/// remote-play-specific probe is SRCH (see Ripcord.Protocol.Halyard.Common).
/// </summary>
public sealed class MdnsBrowser
{
    private static readonly IPEndPoint MulticastEndpoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    /// <summary>Query <paramref name="serviceName"/> (PTR) and gather A records for <paramref name="window"/>.</summary>
    public async Task<IReadOnlyList<MdnsHost>> BrowseAsync(
        string serviceName,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        udp.JoinMulticastGroup(MulticastEndpoint.Address);

        byte[] query = BuildPtrQuery(serviceName);
        await udp.SendAsync(query, MulticastEndpoint, cancellationToken).ConfigureAwait(false);

        var hosts = new Dictionary<string, MdnsHost>(StringComparer.OrdinalIgnoreCase);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(window);

        try
        {
            while (!cts.IsCancellationRequested)
            {
                UdpReceiveResult result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                foreach (MdnsHost host in ParseARecords(result.Buffer))
                {
                    hosts[host.HostName] = host;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // window elapsed
        }

        return [.. hosts.Values];
    }

    private static byte[] BuildPtrQuery(string serviceName)
    {
        var buffer = new List<byte>(64);
        buffer.AddRange(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }); // header: 1 question
        foreach (string label in serviceName.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] bytes = Encoding.ASCII.GetBytes(label);
            buffer.Add((byte)bytes.Length);
            buffer.AddRange(bytes);
        }

        buffer.Add(0);              // name terminator
        buffer.AddRange(new byte[] { 0, 12 }); // QTYPE = PTR
        buffer.AddRange(new byte[] { 0, 1 });  // QCLASS = IN
        return [.. buffer];
    }

    private static IEnumerable<MdnsHost> ParseARecords(byte[] message)
    {
        var results = new List<MdnsHost>();
        if (message.Length < 12)
        {
            return results;
        }

        int qd = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(4));
        int an = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(6));
        int offset = 12;

        for (int i = 0; i < qd; i++)
        {
            SkipName(message, ref offset);
            offset += 4; // qtype + qclass
        }

        for (int i = 0; i < an && offset + 10 <= message.Length; i++)
        {
            string name = ReadName(message, ref offset);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset));
            offset += 8; // type(2) + class(2) + ttl(4)
            int rdlen = BinaryPrimitives.ReadUInt16BigEndian(message.AsSpan(offset));
            offset += 2;

            if (type == 1 && rdlen == 4 && offset + 4 <= message.Length) // A record
            {
                results.Add(new MdnsHost(name.TrimEnd('.'), new IPAddress(message.AsSpan(offset, 4).ToArray())));
            }

            offset += rdlen;
        }

        return results;
    }

    private static void SkipName(byte[] message, ref int offset)
    {
        while (offset < message.Length)
        {
            byte len = message[offset];
            if (len == 0) { offset++; return; }
            if ((len & 0xC0) == 0xC0) { offset += 2; return; } // compression pointer ends the name
            offset += len + 1;
        }
    }

    private static string ReadName(byte[] message, ref int offset)
    {
        var sb = new StringBuilder();
        int position = offset;
        bool jumped = false;

        while (position < message.Length)
        {
            byte len = message[position];
            if (len == 0)
            {
                position++;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                int pointer = ((len & 0x3F) << 8) | message[position + 1];
                if (!jumped) { offset = position + 2; }
                position = pointer;
                jumped = true;
                continue;
            }

            sb.Append(Encoding.ASCII.GetString(message, position + 1, len)).Append('.');
            position += len + 1;
        }

        if (!jumped) { offset = position; }
        return sb.ToString();
    }
}
