using Ripcord.Core.Accounts;
using Ripcord.Core.Platform;

namespace Ripcord.Cloud.Halyard;

/// <summary>Who is signed in, as far as the cloud tier is concerned.</summary>
/// <param name="AccountId">
/// The account identifier. Decimal, and already in the form the console pairing exchange wants —
/// <c>HalyardRegistrationMessage</c> parses exactly this and encodes it to the 8 bytes that travel inside the
/// encrypted registration body, which is what makes cloud sign-in able to remove the hand-typed account id.
/// </param>
public sealed record HalyardAccount(string AccountId, string OnlineId, string Region);

/// <summary>
/// The sign-in lifecycle, and the one thing that was missing from this assembly: a caller for
/// <see cref="HalyardAuthClient.ExchangeCodeAsync"/>.
///
/// <para>
/// Everything under it already existed — authorize-URL construction, the code exchange, the refresh grant, the
/// auto-refreshing token provider, the account lookup. What did not exist was anything that put them in order and
/// wrote the result down, so the only way to reach the cloud tier was to hand it a refresh token obtained by
/// other means. This type is that order:
/// </para>
///
/// <list type="number">
///   <item><description><see cref="BeginSignIn"/> — the URL a web view should open.</description></item>
///   <item><description><see cref="CompleteSignInAsync"/> — the redirect it landed on, exchanged for tokens,
///   the account identified, and the refresh token persisted.</description></item>
///   <item><description><see cref="RestoreAsync"/> — on later launches, the stored refresh token traded for a
///   live session with no user interaction.</description></item>
///   <item><description><see cref="SignOut"/> — the stored credential destroyed.</description></item>
/// </list>
///
/// <para>
/// <b>Not thread-safe, and not marshalled.</b> It holds mutable session state and takes no locks, matching the
/// app layer's rule that one dispatcher owns everything: callers in the app reach it through the presentation
/// seam, which is dispatcher-confined. The console harness is single-threaded and calls it directly.
/// </para>
/// </summary>
public sealed class HalyardAccountGateway
{
    private readonly HalyardAuthClient _auth;
    private readonly HalyardTokenProvider _tokens;
    private readonly HalyardCloudClient _cloud;
    private readonly IAccountTokenStore _store;
    private readonly HalyardClientConfig _config;
    private readonly string _clientDeviceId;

    private HalyardAccount? _account;

    public HalyardAccountGateway(
        HttpClient http,
        HalyardClientConfig config,
        IAccountTokenStore store,
        IDeviceIdentity deviceIdentity)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(deviceIdentity);

        _config = config;
        _store = store;
        _auth = new HalyardAuthClient(http, config);
        _tokens = new HalyardTokenProvider(_auth);
        _cloud = new HalyardCloudClient(http, _tokens);

        // Resolved once, eagerly, so a machine that cannot supply a device id fails at construction with a
        // sentence about the machine rather than midway through a sign-in the user has already started.
        _clientDeviceId = config.IsConfigured ? HalyardClientDeviceId.For(deviceIdentity) : string.Empty;
    }

    /// <summary>Whether this build has a credential at all. False means every method below will decline.</summary>
    public bool CanSignIn => _config.IsConfigured;

    /// <summary>The signed-in account, or null. Populated by a successful sign-in or restore.</summary>
    public HalyardAccount? Account => _account;

    /// <summary>True once there is a usable session.</summary>
    public bool IsSignedIn => _account is not null;

    /// <summary>
    /// The authenticated cloud surface. Only meaningful once signed in; calls will throw from the token provider
    /// otherwise, which is the correct outcome — an unsigned-in caller asking for the console list is a bug at
    /// the call site, not a state to degrade around.
    /// </summary>
    public HalyardCloudClient Cloud => _cloud;

    /// <summary>
    /// A currently-valid access token, refreshing if needed. Used by callers that must present the token
    /// somewhere other than the cloud REST surface — the push WebSocket upgrade in particular.
    /// </summary>
    public Task<string> AccessTokenAsync(CancellationToken cancellationToken)
        => _tokens.GetAccessTokenAsync(cancellationToken);

    /// <summary>Whether a stored credential exists to restore from, without touching the network.</summary>
    public bool HasStoredSession => _store.Load() is not null;

    /// <summary>The URL to open in the account web flow.</summary>
    public string BeginSignIn()
    {
        if (!CanSignIn)
        {
            throw new InvalidOperationException(
                "No OAuth client credential is configured, so sign-in cannot start. Check CanSignIn first.");
        }

        return _auth.BuildAuthorizeUrl(_clientDeviceId);
    }

    /// <summary>
    /// Whether <paramref name="redirected"/> is the redirect carrying the authorization code. A web view asks
    /// this on each navigation so it knows when the flow is done.
    /// </summary>
    public bool IsCompletionRedirect(Uri redirected) => _auth.TryReadAuthorizationCode(redirected) is not null;

    /// <summary>
    /// Finish a sign-in from the redirect the web view landed on: exchange the code, identify the account, and
    /// persist the refresh token.
    /// </summary>
    /// <returns>The signed-in account.</returns>
    /// <exception cref="HalyardCloudException">
    /// When the redirect carries no code, or the exchange or account lookup fails.
    /// </exception>
    public async Task<HalyardAccount> CompleteSignInAsync(Uri redirected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(redirected);

        string code = _auth.TryReadAuthorizationCode(redirected)
            ?? throw new HalyardCloudException("That redirect carried no authorization code.");

        HalyardTokens tokens = await _auth
            .ExchangeCodeAsync(code, _clientDeviceId, cancellationToken)
            .ConfigureAwait(false);
        _tokens.Seed(tokens);

        return await IdentifyAndPersistAsync(tokens, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Re-establish a session from the stored refresh token. Returns null when there is nothing stored, or when
    /// the stored token is no longer good — both of which mean "show the sign-in affordance", and neither of
    /// which is exceptional enough to throw about.
    ///
    /// <para>
    /// A rejected refresh token clears the store. Leaving it in place would mean every launch spending a network
    /// round trip re-learning that it is dead, and the user seeing a sign-in prompt while a file on disk claims
    /// otherwise.
    /// </para>
    /// </summary>
    public async Task<HalyardAccount?> RestoreAsync(CancellationToken cancellationToken)
    {
        if (!CanSignIn)
        {
            return null;
        }

        StoredAccountSession? stored = _store.Load();
        if (stored is null)
        {
            return null;
        }

        try
        {
            await _tokens.SeedFromRefreshTokenAsync(stored.RefreshToken, cancellationToken).ConfigureAwait(false);
        }
        catch (HalyardCloudException)
        {
            _store.Clear();
            return null;
        }

        HalyardTokens current = _tokens.Current
            ?? throw new HalyardCloudException("The refresh succeeded but produced no tokens.");

        try
        {
            return await IdentifyAndPersistAsync(current, cancellationToken).ConfigureAwait(false);
        }
        catch (HalyardCloudException)
        {
            // The token refreshed but the account lookup failed — a service problem rather than a credential
            // one. Fall back to the cached identity so an install stays signed in through an outage.
            if (stored.AccountId is null)
            {
                return null;
            }

            _account = new HalyardAccount(stored.AccountId, stored.DisplayName ?? string.Empty, string.Empty);
            return _account;
        }
    }

    /// <summary>Destroy the stored credential and forget the session.</summary>
    public void SignOut()
    {
        _store.Clear();
        _account = null;
    }

    private async Task<HalyardAccount> IdentifyAndPersistAsync(
        HalyardTokens tokens, CancellationToken cancellationToken)
    {
        HalyardAccountInfo info = await _cloud.GetAccountInfoAsync(cancellationToken).ConfigureAwait(false);
        var account = new HalyardAccount(info.AccountId, info.OnlineId, info.Region);
        _account = account;

        // Persist the refresh token the provider currently holds rather than the one we started with: a refresh
        // grant returns a NEW refresh token, and storing the spent one is how an install signs itself out
        // roughly an hour later for no visible reason.
        HalyardTokens latest = _tokens.Current ?? tokens;
        _store.Save(new StoredAccountSession(
            latest.RefreshToken, account.AccountId, account.OnlineId, DateTimeOffset.UtcNow));

        return account;
    }
}
