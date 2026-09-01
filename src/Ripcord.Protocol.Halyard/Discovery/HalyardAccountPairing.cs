using Ripcord.Cloud.Halyard;
using Ripcord.Cloud.Halyard.Rendezvous;
using Ripcord.Protocol.Halyard.Common.Crypto;
using Ripcord.Protocol.Halyard.Common.Crypto.V1;

namespace Ripcord.Protocol.Halyard.Discovery;

/// <summary>What identifies the console and account for an account ("web"/no-PIN) pairing.</summary>
/// <param name="ConsoleId">The stored console id (for the pairing record).</param>
/// <param name="ConsoleHost">The console's reachable host — from LAN discovery or a WAN rendezvous candidate —
/// for the direct <c>/sess/rgst</c> POST.</param>
/// <param name="ConsoleDuid">The console's device unique id (from the cloud console list).</param>
/// <param name="AccountId">The signed-in account id.</param>
/// <param name="ClientDeviceId">This device's id (the RP-Did material).</param>
public sealed record HalyardAccountPairingRequest(
    string ConsoleId,
    string ConsoleHost,
    string ConsoleDuid,
    string AccountId,
    ReadOnlyMemory<byte> ClientDeviceId,
    HalyardConsolePlatform Platform = HalyardConsolePlatform.Ps5,
    string ClientType = "Windows");

/// <summary>Tunables for account pairing.</summary>
public sealed class HalyardAccountPairingOptions
{
    /// <summary>How long to wait for the console to publish <c>customData1</c> (the seed) after the command.</summary>
    public TimeSpan SeedTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional progress sink for a harness (push connected, session, command, seed, register).</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// Drives account ("web"/no-PIN) pairing end to end: the console delivers the registration seed encrypted over
/// the cloud, and this coordinates receiving it and turning it into a pairing record.
///
/// <para>
/// The sequence — the whole point of this type:
/// </para>
/// <list type="number">
///   <item><description>Generate ephemeral <c>data1</c>/<c>data2</c> (the seed-delivery key/material).</description></item>
///   <item><description>Start the push channel and subscribe to <c>customData1</c> <b>before</b> triggering the
///   console, so the seed cannot arrive unheard.</description></item>
///   <item><description>Create the session and send the connect command carrying <c>data1</c>/<c>data2</c>.</description></item>
///   <item><description>The console field-encrypts the seed with them and publishes it as <c>customData1</c>;
///   recover it (<see cref="HalyardAccountSeedDelivery"/>).</description></item>
///   <item><description>Run the direct <c>/sess/rgst</c> registration with that seed and return the pairing
///   record.</description></item>
/// </list>
///
/// <para>
/// The push channel is passed in not-yet-running (the caller built it over a real or fake WebSocket), which is
/// what lets the whole flow be driven from captured/scripted frames in tests. Its socket remains the caller's
/// to dispose; this type only runs and then stops the receive loop.
/// </para>
/// </summary>
public sealed class HalyardAccountPairing(
    IHalyardSignalingClient signaling,
    IHalyardRegistration registration,
    ReadOnlyMemory<byte> contextKey,
    HalyardAccountPairingOptions? options = null)
{
    private readonly IHalyardSignalingClient _signaling = signaling ?? throw new ArgumentNullException(nameof(signaling));
    private readonly IHalyardRegistration _registration = registration ?? throw new ArgumentNullException(nameof(registration));
    private readonly byte[] _contextKey = contextKey.Length == 16
        ? contextKey.ToArray()
        : throw new ArgumentException("Context key must be 16 bytes.", nameof(contextKey));
    private readonly HalyardAccountPairingOptions _options = options ?? new HalyardAccountPairingOptions();

    /// <summary>
    /// Pair an account console. <paramref name="pushChannel"/> is a not-yet-running channel over a WebSocket
    /// the caller owns; <paramref name="pushServer"/> and <paramref name="accessToken"/> drive its upgrade.
    /// </summary>
    public async Task<HalyardRegistrationResult> PairAsync(
        HalyardAccountPairingRequest request,
        HalyardPushChannel pushChannel,
        HalyardPushServerInfo pushServer,
        string accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(pushChannel);
        ArgumentNullException.ThrowIfNull(pushServer);

        (byte[] data1, byte[] data2) = HalyardAccountSeedDelivery.GenerateEphemeralKeyMaterial();

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Completed by the first customData1 that decrypts under our data1/data2. A foreign or malformed one is
        // ignored so a stray frame cannot resolve the wait with garbage.
        var seed = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnCustomData1(string customData1)
        {
            try
            {
                seed.TrySetResult(HalyardAccountSeedDelivery.RecoverSeed(data1, data2, customData1, _contextKey));
            }
            catch (FormatException) { /* not a valid double-base64 customData1 for us — keep waiting */ }
            catch (ArgumentException) { /* wrong length after decode — keep waiting */ }
        }

        pushChannel.CustomData1Received += OnCustomData1;

        // Start receiving before triggering the console, so a customData1 published the instant it joins is not
        // missed. The loop runs until lifetime is cancelled or the peer closes.
        Task pushLoop = pushChannel.RunAsync(pushServer, accessToken, lifetime.Token);

        try
        {
            // The session's message channel binds to the live push connection at create time, so the push
            // WebSocket must be up before the session is created (mirrors the vendor ordering). A failed upgrade
            // surfaces here.
            await pushChannel.Connected.WaitAsync(cancellationToken).ConfigureAwait(false);
            Log("push channel connected");

            string sessionId = await _signaling
                .CreateSessionAsync(Guid.NewGuid().ToString(), cancellationToken)
                .ConfigureAwait(false);
            Log($"session created: {sessionId}");

            await _signaling.SendConnectCommandAsync(
                request.ConsoleDuid, request.AccountId, sessionId, request.ClientType,
                (Convert.ToBase64String(data1), Convert.ToBase64String(data2), string.Empty), cancellationToken)
                .ConfigureAwait(false);
            Log("connect command sent (data1/data2)");

            byte[] recoveredSeed;
            try
            {
                recoveredSeed = await seed.Task.WaitAsync(_options.SeedTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return new HalyardRegistrationResult(false,
                    "The console did not publish the registration seed (customData1) in time.", null);
            }

            Log("registration seed recovered from customData1");

            var registrationRequest = new HalyardRegistrationRequest(
                request.ConsoleId, request.ConsoleHost, request.AccountId,
                Passcode: string.Empty, request.ClientDeviceId, request.Platform)
            {
                AccountSeed = recoveredSeed,
            };

            HalyardRegistrationResult result = await _registration
                .RegisterAsync(registrationRequest, cancellationToken)
                .ConfigureAwait(false);
            Log(result.Succeeded ? "registered" : $"registration failed: {result.FailureReason}");
            return result;
        }
        finally
        {
            pushChannel.CustomData1Received -= OnCustomData1;
            await lifetime.CancelAsync().ConfigureAwait(false);
            await SafeAwaitAsync(pushLoop).ConfigureAwait(false);
        }
    }

    private void Log(string message) => _options.Log?.Invoke(message);

    private static async Task SafeAwaitAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // The push loop is being torn down; its exit reason is not this method's concern.
        }
    }
}
