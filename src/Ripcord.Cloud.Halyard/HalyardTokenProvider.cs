namespace Ripcord.Cloud.Halyard;

/// <summary>
/// Holds the account tokens and yields a valid access token for API calls, refreshing transparently
/// when the access token has expired. Seed it with tokens from a fresh sign-in or a persisted
/// refresh token.
/// </summary>
public sealed class HalyardTokenProvider(HalyardAuthClient auth)
{
    private readonly HalyardAuthClient _auth = auth;
    private HalyardTokens? _tokens;

    public HalyardTokens? Current => _tokens;

    public void Seed(HalyardTokens tokens) => _tokens = tokens;

    /// <summary>Seed from a persisted refresh token by performing a refresh immediately.</summary>
    public async Task SeedFromRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
        => _tokens = await _auth.RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_tokens is null)
        {
            throw new InvalidOperationException("Not signed in - seed the token provider first.");
        }

        if (_tokens.IsExpired)
        {
            _tokens = await _auth.RefreshAsync(_tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
        }

        return _tokens.AccessToken;
    }
}
