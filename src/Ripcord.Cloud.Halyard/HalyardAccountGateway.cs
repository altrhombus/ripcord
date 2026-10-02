using System.Text.Json;
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
        _tokens.OnRefreshed(PersistRefreshed);
        _cloud = new HalyardCloudClient(http, _tokens);

        // Resolved once, eagerly, so a machine that cannot supply a device id fails at construction with a
        // sentence about the machine rather than midway through a sign-in the user has already started.
        _clientDeviceId = config.IsConfigured ? HalyardClientDeviceId.For(deviceIdentity) : string.Empty;
    }

    /// <summary>Whether this build has a credential at all. False means every method below will decline.</summary>
    public bool CanSignIn => _config.IsConfigured;

    /// <summary>The signed-in account, or null. Populated by a successful sign-in or restore.</summary>
    public HalyardAccount? Account => _account;

    /// <summary>True once there is a usable session <em>in memory</em>.</summary>
    ///
    /// <para>
    /// <b>Not the same question as "is this user signed in".</b> A stored refresh token is a signed-in user
    /// whose session has not been restored yet, and this reports false for them. Anything deciding whether a
    /// feature is <em>available</em> wants <see cref="EnsureSignedInAsync"/>; this is for surfaces that are
    /// already showing state and want to know what is loaded.
    /// </para>
    public bool IsSignedIn => _account is not null;

    /// <summary>
    /// Make sure the session is loaded, restoring it from the stored refresh token if it is not, and report
    /// whether there is one. Safe to call repeatedly and from anywhere; the restore happens at most once.
    ///
    /// <para>
    /// <b>The bug this exists for.</b> Nothing restored the session except the settings page. A user who
    /// launched Ripcord and connected to a console straight away had a gateway with no account in memory, so
    /// the account route refused with "sign in to your PlayStation Network account" — to somebody who was
    /// signed in, and whose stored token was perfectly good. Remote play from outside your own network worked
    /// only if you happened to open Settings first. Found on a phone hotspot, after the route had already
    /// been confirmed working.
    /// </para>
    ///
    /// <para>
    /// Restoring lazily rather than at startup keeps launch free of a network round trip that most sessions
    /// never need — a LAN connect does not touch the account tier at all.
    /// </para>
    /// </summary>
    public async Task<bool> EnsureSignedInAsync(CancellationToken cancellationToken)
    {
        if (_account is not null)
        {
            return true;
        }

        if (!CanSignIn || !HasStoredSession)
        {
            return false;
        }

        try
        {
            return await RestoreAsync(cancellationToken).ConfigureAwait(false) is not null;
        }
        catch (Exception)
        {
            // A failed restore is "not signed in", which is what the caller asked. It is not this method's
            // business to decide whether that is worth reporting.
            return false;
        }
    }

    /// <summary>
    /// The authenticated cloud surface. Only meaningful once signed in; calls will throw from the token provider
    /// otherwise, which is the correct outcome — an unsigned-in caller asking for the console list is a bug at
    /// the call site, not a state to degrade around.
    /// </summary>
    public HalyardCloudClient Cloud => _cloud;

    /// <summary>
    /// The client device id this gateway signs in with. Exposed because the signaling <c>localHashedId</c> must
    /// derive from the same one — a client that announced an id unrelated to the device it authenticated as
    /// would be two different peers as far as the console is concerned. Empty when this build has no
    /// credential and never formed one.
    /// </summary>
    public string ClientDeviceId => _clientDeviceId;

    /// <summary>
    /// A currently-valid access token, refreshing if needed. Used by callers that must present the token
    /// somewhere other than the cloud REST surface — the push WebSocket upgrade in particular.
    /// </summary>
    public Task<string> AccessTokenAsync(CancellationToken cancellationToken)
        => _tokens.GetAccessTokenAsync(cancellationToken);

    /// <summary>Whether a stored credential exists to restore from, without touching the network.</summary>
    public bool HasStoredSession => _store.Load() is not null;

    private readonly object _restoreGate = new();
    private Task<HalyardAccount?>? _restore;

    // Bumped by SignOut, under _restoreGate. A sign-in or restore captures it when it starts and commits its
    // result (the stored session, the account) only while it is unchanged, also under _restoreGate. So a sign-out
    // landing mid-restore is final: the restore's later save used to put the account straight back (the second
    // 2026-10-01 review).
    private int _signInGeneration;

    /// <summary>Run <paramref name="commit"/> only if no sign-out has happened since <paramref name="generation"/>.</summary>
    private bool CommitIfCurrent(int generation, Action commit)
    {
        lock (_restoreGate)
        {
            if (_signInGeneration != generation)
            {
                return false;
            }

            commit();
            return true;
        }
    }

    private int CurrentGeneration()
    {
        lock (_restoreGate)
        {
            return _signInGeneration;
        }
    }

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

        int generation = CurrentGeneration();
        HalyardTokens tokens = await _auth
            .ExchangeCodeAsync(code, _clientDeviceId, cancellationToken)
            .ConfigureAwait(false);
        _tokens.Seed(tokens);

        try
        {
            return await IdentifyAndPersistAsync(tokens, generation, cancellationToken).ConfigureAwait(false)
                ?? throw new HalyardCloudException("Signed out while the sign-in was finishing.");
        }
        catch (Exception)
        {
            // The sign-in failed, so it must not half-succeed: the provider stayed seeded with the new tokens, and
            // every cloud call after a "sign-in failed" message went out as that account anyway.
            _tokens.Clear();
            _account = null;
            throw;
        }
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
    ///
    /// <para>
    /// <b>Only a rejected one.</b> Any cloud failure used to clear it, so a 503 at launch, or a token endpoint
    /// having a bad minute, signed the install out for good (the 2026-09-30 review). Now a failure that says
    /// nothing about the credential returns null and keeps the store, and the next attempt tries again.
    /// </para>
    /// </summary>
    ///
    /// <para>
    /// <b>One restore at a time, from every caller.</b> A connect and a console-list refresh racing each other
    /// would both spend the refresh token, and the second would find it rotated. That guard used to live in
    /// <see cref="EnsureSignedInAsync"/> alone, while Settings called this directly on every visit, so the two
    /// could overlap: the loser's rejection then cleared the winner's freshly stored sign-in (the second
    /// 2026-10-01 review). Now the guard is here, an account already signed in is returned without touching the
    /// network, and <see cref="EnsureSignedInAsync"/> goes through it too.
    /// </para>
    /// </summary>
    public async Task<HalyardAccount?> RestoreAsync(CancellationToken cancellationToken)
    {
        if (_account is { } signedIn)
        {
            return signedIn;
        }

        Task<HalyardAccount?> restore;
        lock (_restoreGate)
        {
            restore = _restore ??= RestoreOnceAsync(cancellationToken);
        }

        try
        {
            return await restore.ConfigureAwait(false);
        }
        finally
        {
            lock (_restoreGate)
            {
                // Kept only while it holds a signed-in result (and SignOut clears it then); a restore that failed
                // or found nothing can be tried again.
                if (ReferenceEquals(_restore, restore) && restore.IsCompleted && _account is null)
                {
                    _restore = null;
                }
            }
        }
    }

    private async Task<HalyardAccount?> RestoreOnceAsync(CancellationToken cancellationToken)
    {
        if (!CanSignIn)
        {
            return null;
        }

        int generation = CurrentGeneration();

        StoredAccountSession? stored = _store.Load();
        if (stored is null)
        {
            return null;
        }

        try
        {
            await _tokens.SeedFromRefreshTokenAsync(stored.RefreshToken, cancellationToken).ConfigureAwait(false);
        }
        catch (HalyardCloudException ex)
        {
            if (ex.IsCredentialRejected)
            {
                _store.Clear();
            }

            return null;
        }
        catch (Exception ex) when (IsNetworkFailure(ex, cancellationToken))
        {
            return null;   // nothing learned about the token, so the store stays as it is
        }

        HalyardTokens current = _tokens.Current
            ?? throw new HalyardCloudException("The refresh succeeded but produced no tokens.");

        // Stored now, before anything else can fail. The refresh spent the stored token, so from here on the store
        // must hold the new one whatever happens next; a network drop during the account lookup used to escape the
        // fallback below and leave the spent one on disk (the second 2026-10-01 review).
        if (!CommitIfCurrent(generation, () =>
                _store.Save(stored with { RefreshToken = current.RefreshToken, SavedAt = DateTimeOffset.UtcNow })))
        {
            // Signed out while the refresh was in flight, and the refresh then seeded the provider anyway. Unseed
            // it, unless a newer sign-in has finished since, whose tokens those now are.
            lock (_restoreGate)
            {
                if (_account is null)
                {
                    _tokens.Clear();
                }
            }

            return null;
        }

        try
        {
            return await IdentifyAndPersistAsync(current, generation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HalyardCloudException or JsonException || IsNetworkFailure(ex, cancellationToken))
        {
            // The token refreshed but the account lookup failed — a service problem rather than a credential
            // one. Fall back to the cached identity so an install stays signed in through an outage.
            //
            // The refresh spent the stored token either way, so the new one is stored here too. This path used
            // to keep the spent one, and the launch after an account-lookup outage was signed out.
            HalyardTokens latest = _tokens.Current ?? current;
            HalyardAccount? cached = stored.AccountId is null
                ? null
                : new HalyardAccount(stored.AccountId, stored.DisplayName ?? string.Empty, string.Empty);

            bool committed = CommitIfCurrent(generation, () =>
            {
                _store.Save(stored with { RefreshToken = latest.RefreshToken, SavedAt = DateTimeOffset.UtcNow });
                _account = cached;
            });

            return committed ? cached : null;
        }
    }

    /// <summary>Destroy the stored credential and forget the session.</summary>
    public void SignOut()
    {
        lock (_restoreGate)
        {
            // The generation first, so a sign-in or restore still running commits nothing after this.
            _signInGeneration++;

            // Then the provider: clearing it is what stops a refresh in flight from storing its tokens
            // (OnRefreshed), so the store cleared after it stays cleared.
            _tokens.Clear();   // or the next cloud call still goes out as the signed-out account
            _store.Clear();
            _account = null;

            // And the finished restore, or the next RestoreAsync would hand back the account just signed out.
            _restore = null;
        }
    }

    /// <summary>
    /// The network failed rather than the service answering: no connection, or a request timing out. A
    /// cancellation the caller asked for is not one, and still propagates.
    /// </summary>
    private static bool IsNetworkFailure(Exception ex, CancellationToken cancellationToken)
        => ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested);

    /// <summary>
    /// Store the refresh token a background refresh just received, keeping the stored identity. Without this the
    /// store held the token from sign-in or restore, which the first refresh spent.
    /// </summary>
    private void PersistRefreshed(HalyardTokens fresh)
    {
        try
        {
            StoredAccountSession? stored = _store.Load();
            _store.Save(new StoredAccountSession(
                fresh.RefreshToken,
                _account?.AccountId ?? stored?.AccountId,
                _account?.OnlineId ?? stored?.DisplayName,
                DateTimeOffset.UtcNow));
        }
        catch (Exception)
        {
            // The refresh itself worked, and every caller sharing it is waiting on the token, not on the disk.
            // A store that can't be written (a locked file, a profile without DPAPI) costs the next launch its
            // sign-in, which is what happened every time before this existed; failing the refresh would cost
            // this session too.
        }
    }

    /// <summary>Identify the account and store the session; null when a sign-out overtook it.</summary>
    private async Task<HalyardAccount?> IdentifyAndPersistAsync(
        HalyardTokens tokens, int generation, CancellationToken cancellationToken)
    {
        HalyardAccountInfo info = await _cloud.GetAccountInfoAsync(cancellationToken).ConfigureAwait(false);
        var account = new HalyardAccount(info.AccountId, info.OnlineId, info.Region);

        // Persist the refresh token the provider currently holds rather than the one we started with: a refresh
        // grant returns a NEW refresh token, and storing the spent one is how an install signs itself out
        // roughly an hour later for no visible reason.
        HalyardTokens latest = _tokens.Current ?? tokens;
        bool committed = CommitIfCurrent(generation, () =>
        {
            _account = account;
            _store.Save(new StoredAccountSession(
                latest.RefreshToken, account.AccountId, account.OnlineId, DateTimeOffset.UtcNow));
        });

        return committed ? account : null;
    }
}
