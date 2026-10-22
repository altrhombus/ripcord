using System.Net;
using Ripcord.Core.Discovery;
using Ripcord.Core.Sessions;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;
using Ripcord.Protocol.Halyard.Common.Crypto.V2;
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
/// When the captures-folder control secrets are present the real <see cref="HalyardV1SessionCrypto"/> is used;
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

    /// <summary>True when the real control crypto is available (captures-folder secrets injected).</summary>
    public bool HasRealCrypto => _controlSecrets is not null;

    /// <summary>Build the default factory: load the control secrets from the fixture and use the per-user
    /// file credential store. <paramref name="cryptoSource"/> reports where the secrets came from (or why not).</summary>
    public static HalyardSessionFactory CreateDefault(out string cryptoSource)
    {
        HalyardControlSecrets? secrets = HalyardControlSecretsLoader.Load(out cryptoSource);
        return new HalyardSessionFactory(secrets, HalyardPairingCredentialStore.ForCurrentUser());
    }

    /// <summary>Create a session from fully-specified connection parameters, over LAN TCP.</summary>
    public IStreamingSession Create(
        HalyardConnectionParameters parameters,
        Func<bool, CancellationToken, Task<string?>>? loginPinProvider = null)
        => Create(parameters, new HalyardTcpControlChannel(), loginPinProvider);

    /// <summary>
    /// Create a session over a control channel the caller supplies.
    ///
    /// <para>
    /// The account ("web"/no-PIN) route needs this: its console answers <c>/sess/init</c> and
    /// <c>/sess/ctrl</c> on the 9303 association that already carried <c>/sess/rgst</c> and refuses TCP
    /// 9295, so the caller passes a <see cref="HalyardDatagramSessionControlChannel"/> wrapping the
    /// association it established. Everything above the channel is identical, which is the point of the seam.
    /// </para>
    /// </summary>
    public IStreamingSession Create(
        HalyardConnectionParameters parameters,
        IHalyardControlChannel control,
        Func<bool, CancellationToken, Task<string?>>? loginPinProvider = null,
        TimeSpan? controlPlaneDeadline = null,
        HalyardStreamTransportFactory? streamTransportFactory = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(control);

        IHalyardSessionCrypto crypto = _controlSecrets is null
            ? new PassthroughHalyardSessionCrypto()
            : new HalyardV1SessionCrypto(_controlSecrets);

        // Opt-in v2 diagnostic (RIPCORD_V2_HANDSHAKE): with real control secrets present, also build the v2
        // session crypto and hand it to the session. The session runs the v2 control handshake only when the
        // console's pairing actually selects v2 (HalyardInitSelection: RP-SupportGcm or a held rolled token);
        // otherwise it uses the v1 crypto above, byte-for-byte as before. The v2 handshake path stops at the
        // persistent v2 control channel, which is not yet implemented, so this changes nothing for a normal
        // connect unless the flag is deliberately set against a v2-capable console.
        HalyardV2SessionCrypto? v2Crypto =
            _controlSecrets is not null && V2HandshakeOptIn
                ? new HalyardV2SessionCrypto(_controlSecrets)
                : null;

        return new HalyardStreamingSession(
            parameters, control, crypto, _credentials, loginPinProvider, controlPlaneDeadline,
            streamTransportFactory, v2Crypto);
    }

    /// <summary>
    /// The opt-in for the v2 control-handshake diagnostic path (<c>RIPCORD_V2_HANDSHAKE</c>). Off by default:
    /// the live v2 path stops at the persistent control channel (not yet implemented), so it is gated behind this
    /// flag and never changes a normal (v1) connect.
    /// </summary>
    internal static bool V2HandshakeOptIn =>
        Environment.GetEnvironmentVariable("RIPCORD_V2_HANDSHAKE") is { Length: > 0 };

    /// <summary>Convenience overload: build the parameters for a console reached at <paramref name="address"/>,
    /// using the standard control/stream ports. <paramref name="consoleId"/> keys the credential store.
    /// <paramref name="loginPinProvider"/> supplies the console login passcode if the console is locked.</summary>
    public IStreamingSession Create(
        string consoleId,
        IPAddress address,
        ReadOnlyMemory<byte> deviceId = default,
        int controlPort = DefaultControlPort,
        int streamPort = DefaultStreamPort,
        Func<bool, CancellationToken, Task<string?>>? loginPinProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(consoleId);
        ArgumentNullException.ThrowIfNull(address);
        var parameters = new HalyardConnectionParameters(
            consoleId,
            new IPEndPoint(address, controlPort),
            new IPEndPoint(address, streamPort),
            deviceId);
        return Create(parameters, loginPinProvider);
    }
}
