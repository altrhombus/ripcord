namespace Ripcord.Cloud.Halyard;

/// <summary>
/// Holds the account tokens and yields a valid access token for API calls, refreshing transparently
/// when the access token has expired. Seed it with tokens from a fresh sign-in or a persisted
/// refresh token.
///
/// <para><b>One refresh at a time.</b> The refresh token rotates: each refresh grant spends it and returns a new
/// one. Two callers finding the access token expired together used to refresh with the same token, and the
/// second grant spent a token the first had already spent. Concurrent callers now share the one refresh in
/// flight, as the Mac port's <c>TokenProvider</c> does (found by that port, 2026-09-26).</para>
///
/// <para><b>Cleared on sign-out.</b> <see cref="Clear"/> forgets the tokens. Sign-out used to clear only the
/// stored session and leave this seeded, so the next cloud call went out as the signed-out account. A refresh
/// that completes after a clear is discarded rather than signing the account back in.</para>
/// </summary>
public sealed class HalyardTokenProvider(HalyardAuthClient auth)
{
    private readonly HalyardAuthClient _auth = auth;
    private readonly Lock _gate = new();
    private HalyardTokens? _tokens;
    private Task<HalyardTokens>? _refreshing;
    private Action<HalyardTokens>? _refreshed;

    // Bumped by every seed and clear, so a refresh started under one sign-in never lands on another.
    private int _generation;

    public HalyardTokens? Current
    {
        get
        {
            lock (_gate)
            {
                return _tokens;
            }
        }
    }

    public void Seed(HalyardTokens tokens)
    {
        lock (_gate)
        {
            _tokens = tokens;
            _refreshing = null;
            _generation++;
        }
    }

    /// <summary>
    /// Called with the new tokens after each refresh the provider makes on its own, so the new refresh token can
    /// be stored. A refresh grant spends the old one, so a refresh that lived only in memory signed the install
    /// out at its next launch (the 2026-09-30 review). Runs under the provider's lock and only for the sign-in
    /// that started it, so a sign-out that has cleared the provider can never be undone by a late refresh.
    /// </summary>
    public void OnRefreshed(Action<HalyardTokens> persist)
    {
        lock (_gate)
        {
            _refreshed = persist;
        }
    }

    /// <summary>Forget the tokens: the account is signed out.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _tokens = null;
            _refreshing = null;
            _generation++;
        }
    }

    /// <summary>Seed from a persisted refresh token by performing a refresh immediately.</summary>
    public async Task SeedFromRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
        => Seed(await _auth.RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false));

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        Task<HalyardTokens> refresh;
        lock (_gate)
        {
            if (_tokens is null)
            {
                throw new InvalidOperationException("Not signed in - seed the token provider first.");
            }

            if (!_tokens.IsExpired)
            {
                return _tokens.AccessToken;
            }

            refresh = _refreshing ??= RefreshAsync(_tokens.RefreshToken, _generation);
        }

        // Each caller may stop waiting; the shared refresh carries on for the others.
        HalyardTokens fresh = await refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        return fresh.AccessToken;
    }

    private async Task<HalyardTokens> RefreshAsync(string refreshToken, int generation)
    {
        // Off the caller's stack before anything can complete: this is started inside the lock and stored after
        // it returns, so a refresh that finished synchronously (an unconfigured build throws at once) would run
        // its finally first and leave a faulted task cached for every later caller.
        await Task.Yield();
        try
        {
            // Not any one caller's token: cancelling it would fail every caller sharing this refresh, and a grant
            // abandoned mid-flight may already have spent the refresh token.
            HalyardTokens fresh = await _auth.RefreshAsync(refreshToken, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                if (generation != _generation)
                {
                    throw new InvalidOperationException("Signed out while the access token was being refreshed.");
                }

                _tokens = fresh;
                _refreshed?.Invoke(fresh);
                return fresh;
            }
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _refreshing = null;
                }
            }
        }
    }
}
