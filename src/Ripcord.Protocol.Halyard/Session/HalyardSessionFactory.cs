using System.Net;
using Ripcord.Core.Discovery;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Transport;

namespace Ripcord.Protocol.Halyard.Session;

/// <summary>
/// Constructs direct-console <see cref="HalyardStreamingSession"/> instances — the integration seam the
/// connect path was missing (nothing previously built a session). Each <see cref="Create"/> gets a fresh
/// control channel and a fresh session crypto (the crypto is stateful: it holds the control key after
/// <c>EstablishControl</c> and the per-direction stream keys after key agreement), sharing the injected
/// credential store.
///
/// <para>
/// When the dirty-room control secrets are present the real <see cref="HalyardV1SessionCrypto"/> is used;
/// otherwise it falls back to <see cref="PassthroughHalyardSessionCrypto"/> so the handshake is still
/// well-formed for replay/testing but a real console rejects it at the MAC check.
/// </para>
/// </summary>
public sealed class HalyardSessionFactory
{
    /// <summary>Control channel (HTTP/1.1 /sess over TCP) port — wire-confirmed for v1.</summary>
    public const int DefaultControlPort = 9295;

    /// <summary>A/V stream (Takion over UDP) port — wire-confirmed default for v1 LAN.</summary>
    public const int DefaultStreamPort = 9296;

    private readonly HalyardControlSecrets? _controlSecrets;
    private readonly IConsoleCredentialStore _credentials;

    public HalyardSessionFactory(HalyardControlSecrets? controlSecrets, IConsoleCredentialStore credentials)
    {
        _controlSecrets = controlSecrets;
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    /// <summary>True when the real control crypto is available (dirty-room secrets injected).</summary>
    public bool HasRealCrypto => _controlSecrets is not null;

    /// <summary>Build the default factory: load the control secrets from the fixture and use the per-user
    /// file credential store. <paramref name="cryptoSource"/> reports where the secrets came from (or why not).</summary>
    public static HalyardSessionFactory CreateDefault(out string cryptoSource)
    {
        HalyardControlSecrets? secrets = HalyardControlSecretsLoader.Load(out cryptoSource);
        return new HalyardSessionFactory(secrets, HalyardPairingCredentialStore.ForCurrentUser());
    }

    /// <summary>Create a session from fully-specified connection parameters.</summary>
    public IStreamingSession Create(HalyardConnectionParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        IHalyardSessionCrypto crypto = _controlSecrets is null
            ? new PassthroughHalyardSessionCrypto()
            : new HalyardV1SessionCrypto(_controlSecrets);
        return new HalyardStreamingSession(parameters, new HalyardTcpControlChannel(), crypto, _credentials);
    }

    /// <summary>Convenience overload: build the parameters for a console reached at <paramref name="address"/>,
    /// using the standard control/stream ports. <paramref name="consoleId"/> keys the credential store.</summary>
    public IStreamingSession Create(
        string consoleId,
        IPAddress address,
        ReadOnlyMemory<byte> deviceId = default,
        int controlPort = DefaultControlPort,
        int streamPort = DefaultStreamPort)
    {
        ArgumentException.ThrowIfNullOrEmpty(consoleId);
        ArgumentNullException.ThrowIfNull(address);
        var parameters = new HalyardConnectionParameters(
            consoleId,
            new IPEndPoint(address, controlPort),
            new IPEndPoint(address, streamPort),
            deviceId);
        return Create(parameters);
    }
}
