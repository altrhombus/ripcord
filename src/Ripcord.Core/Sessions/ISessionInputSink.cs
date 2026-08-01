using Ripcord.Core.Input;

namespace Ripcord.Core.Sessions;

/// <summary>
/// Owned by the protocol backend: serializes and encrypts the neutral controller frame into its
/// own wire format and sends it to the console.
/// </summary>
public interface ISessionInputSink
{
    void SubmitControllerState(ControllerStateFrame frame);
}
