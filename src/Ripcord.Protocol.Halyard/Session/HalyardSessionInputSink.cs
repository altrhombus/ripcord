using Ripcord.Core.Input;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Input;

namespace Ripcord.Protocol.Halyard.Session;

/// <summary>
/// Serializes neutral controller frames to input packets and sends them on the stream port. The
/// send action is wired by the session to the live stream socket + console endpoint.
/// </summary>
public sealed class HalyardSessionInputSink(HalyardInputPacketWriter writer, Action<byte[]> send) : ISessionInputSink
{
    private readonly HalyardInputPacketWriter _writer = writer;
    private readonly Action<byte[]> _send = send;

    public void SubmitControllerState(ControllerStateFrame frame)
    {
        foreach (byte[] packet in _writer.BuildPackets(frame))
        {
            _send(packet);
        }
    }
}
