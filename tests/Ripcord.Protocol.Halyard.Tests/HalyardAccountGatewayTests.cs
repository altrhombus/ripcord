using System.Net;
using System.Text;
using Ripcord.Cloud.Halyard;
using Ripcord.Core.Accounts;
using Ripcord.Core.Platform;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The 2026-09-30 review's silent sign-out: a refreshed token lived only in memory, and any cloud failure at
/// restore, a 503 included, cleared the stored account. These drive the real gateway against a fake token
/// endpoint and an in-memory store.
/// </summary>
public class HalyardAccountGatewayTests
{
    /// <summary>
    /// Answers token grants (POSTs carrying <c>grant_type</c>) in turn from a script; every other request fails
    /// with 500, so the account lookup falls back to the stored identity.
    /// </summary>
    private sealed class Cloud(params (HttpStatusCode Status, string Body)[] grants) : HttpMessageHandler
    {
        private int _next;

        public int Grants => Volatile.Read(ref _next);

        /// <summary>When set, the account lookup fails like a dropped network instead of answering 500.</summary>
        public bool LookupNetworkFails { get; init; }

        /// <summary>When set, each token grant waits for this, so two restores can genuinely overlap.</summary>
        public TaskCompletionSource? GrantGate { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Method != HttpMethod.Post || !form.Contains("grant_type="))
            {
                if (LookupNetworkFails)
                {
                    throw new HttpRequestException("network unreachable (synthetic)");
                }

                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            int n = Interlocked.Increment(ref _next) - 1;
            if (GrantGate is { } gate)
            {
                await gate.Task.WaitAsync(cancellationToken);
            }

            var (status, body) = grants[Math.Min(n, grants.Length - 1)];
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static (HttpStatusCode, string) Grant(string refresh, int expiresIn = 3600)
        => (HttpStatusCode.OK,
            $"{{\"access_token\":\"access-{refresh}\",\"refresh_token\":\"{refresh}\",\"token_type\":\"bearer\",\"expires_in\":{expiresIn},\"scope\":\"x\"}}");

    private static readonly HalyardClientConfig Config =
        new("id", "secret", HalyardClientConfig.DefaultRedirectUri, HalyardClientConfig.DefaultScopes);

    private static HalyardAccountGateway Gateway(Cloud cloud, IAccountTokenStore store)
        => new(new HttpClient(cloud), Config, store, new StaticDeviceIdentity(Enumerable.Range(1, 16).Select(i => (byte)i).ToArray()));

    private static InMemoryAccountTokenStore SignedIn(string refreshToken)
    {
        var store = new InMemoryAccountTokenStore();
        store.Save(new StoredAccountSession(refreshToken, "account-1", "player", DateTimeOffset.UtcNow));
        return store;
    }

    [Fact]
    public async Task AnOutageAtRestore_KeepsTheStoredSignIn()
    {
        var store = SignedIn("refresh-0");
        var gateway = Gateway(new Cloud((HttpStatusCode.ServiceUnavailable, "")), store);

        Assert.Null(await gateway.RestoreAsync(CancellationToken.None));
        Assert.Equal("refresh-0", store.Load()?.RefreshToken);
    }

    [Fact]
    public async Task ARejectedToken_ClearsTheStore()
    {
        var store = SignedIn("refresh-0");
        var gateway = Gateway(new Cloud((HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")), store);

        Assert.Null(await gateway.RestoreAsync(CancellationToken.None));
        Assert.Null(store.Load());
    }

    /// <summary>
    /// Only invalid_grant says the token is dead. A 401 from a token endpoint is the application's credential being
    /// refused, and another 400 is a malformed request; neither may cost the user their stored sign-in.
    /// </summary>
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}")]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_request\"}")]
    [InlineData(HttpStatusCode.BadRequest, "")]
    public async Task AnythingButInvalidGrant_KeepsTheStore(HttpStatusCode status, string body)
    {
        var store = SignedIn("refresh-0");
        var gateway = Gateway(new Cloud((status, body)), store);

        Assert.Null(await gateway.RestoreAsync(CancellationToken.None));
        Assert.Equal("refresh-0", store.Load()?.RefreshToken);
    }

    /// <summary>
    /// The token rotated, then the network dropped during the account lookup. That threw past the fallback, which
    /// only knew the cloud client's own error, and left the spent token on disk.
    /// </summary>
    [Fact]
    public async Task ANetworkDropAfterTheRefresh_StillStoresTheNewToken()
    {
        var store = SignedIn("refresh-0");
        var gateway = Gateway(new Cloud(Grant("refresh-1")) { LookupNetworkFails = true }, store);

        HalyardAccount? account = await gateway.RestoreAsync(CancellationToken.None);

        Assert.Equal("account-1", account?.AccountId);   // the cached identity
        Assert.Equal("refresh-1", store.Load()?.RefreshToken);
    }

    /// <summary>
    /// Settings restored on every visit, beside the connect path's own restore. Two at once spent one token twice,
    /// and the loser's rejection cleared the winner's sign-in. Now they share one restore and one grant.
    /// </summary>
    [Fact]
    public async Task OverlappingRestores_SpendTheTokenOnce()
    {
        var store = SignedIn("refresh-0");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cloud = new Cloud(Grant("refresh-1"), (HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}"))
        {
            GrantGate = gate,
        };
        var gateway = Gateway(cloud, store);

        Task<HalyardAccount?> settings = gateway.RestoreAsync(CancellationToken.None);
        Task<bool> connect = gateway.EnsureSignedInAsync(CancellationToken.None);
        await Task.Delay(50);
        gate.SetResult();

        Assert.NotNull(await settings);
        Assert.True(await connect);
        Assert.Equal(1, cloud.Grants);
        Assert.Equal("refresh-1", store.Load()?.RefreshToken);
    }

    /// <summary>A sign-out while a restore is in flight is final: the restore's later save put the account back.</summary>
    [Fact]
    public async Task SigningOutDuringARestore_StaysSignedOut()
    {
        var store = SignedIn("refresh-0");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = Gateway(new Cloud(Grant("refresh-1")) { GrantGate = gate }, store);

        Task<HalyardAccount?> restore = gateway.RestoreAsync(CancellationToken.None);
        await Task.Delay(50);
        gateway.SignOut();
        gate.SetResult();

        Assert.Null(await restore);
        Assert.Null(store.Load());
        Assert.False(gateway.IsSignedIn);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AccessTokenAsync(CancellationToken.None));
    }

    /// <summary>A token reply that isn't JSON (a proxy's error page) is a failed refresh, not an escaped JsonException.</summary>
    [Fact]
    public async Task ATokenReplyThatIsNotJson_KeepsTheStore()
    {
        var store = SignedIn("refresh-0");
        var gateway = Gateway(new Cloud((HttpStatusCode.OK, "<html>gateway error</html>")), store);

        Assert.Null(await gateway.RestoreAsync(CancellationToken.None));
        Assert.Equal("refresh-0", store.Load()?.RefreshToken);
    }

    /// <summary>
    /// A sign-in whose account lookup fails must not half-succeed: the provider stayed seeded, so every cloud call
    /// after the "sign-in failed" message went out as that account anyway.
    /// </summary>
    [Fact]
    public async Task AFailedSignIn_LeavesNothingSignedIn()
    {
        var store = new InMemoryAccountTokenStore();
        var gateway = Gateway(new Cloud(Grant("refresh-1")), store);

        await Assert.ThrowsAsync<HalyardCloudException>(() => gateway.CompleteSignInAsync(
            new Uri(HalyardClientConfig.DefaultRedirectUri + "?code=synthetic"), CancellationToken.None));

        Assert.False(gateway.IsSignedIn);
        Assert.Null(store.Load());
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AccessTokenAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ASignedInGateway_RestoresWithoutTheNetwork()
    {
        var store = SignedIn("refresh-0");
        var cloud = new Cloud(Grant("refresh-1"));
        var gateway = Gateway(cloud, store);

        Assert.NotNull(await gateway.RestoreAsync(CancellationToken.None));
        Assert.NotNull(await gateway.RestoreAsync(CancellationToken.None));
        Assert.Equal(1, cloud.Grants);
    }

    [Fact]
    public async Task ARestoreAfterAnOutage_Succeeds()
    {
        var store = SignedIn("refresh-0");
        var gateway = Gateway(new Cloud((HttpStatusCode.ServiceUnavailable, ""), Grant("refresh-1")), store);

        Assert.False(await gateway.EnsureSignedInAsync(CancellationToken.None));
        Assert.True(await gateway.EnsureSignedInAsync(CancellationToken.None));
        Assert.Equal("refresh-1", store.Load()?.RefreshToken);
    }

    /// <summary>
    /// The heart of it: a refresh made later in the session, when the access token expires, spends the stored
    /// refresh token, so the new one has to reach the store or the next launch is signed out.
    /// </summary>
    [Fact]
    public async Task ABackgroundRefresh_IsStored()
    {
        var store = SignedIn("refresh-0");
        // Every grant expires at once (inside the provider's one-minute margin), so each access-token request
        // refreshes. The restore makes two (its own, then the account lookup's); the third, after the restore has
        // finished, can reach the store only through the provider's refresh callback.
        var cloud = new Cloud(Grant("refresh-1", 30), Grant("refresh-2", 30), Grant("refresh-3", 30));
        var gateway = Gateway(cloud, store);

        Assert.NotNull(await gateway.RestoreAsync(CancellationToken.None));
        Assert.Equal(2, cloud.Grants);
        Assert.Equal("refresh-2", store.Load()?.RefreshToken);

        Assert.Equal("access-refresh-3", await gateway.AccessTokenAsync(CancellationToken.None));
        Assert.Equal(3, cloud.Grants);

        StoredAccountSession? stored = store.Load();
        Assert.Equal("refresh-3", stored?.RefreshToken);
        Assert.Equal("account-1", stored?.AccountId);   // the identity survives the refresh
    }

    [Fact]
    public async Task SignOut_StaysSignedOut()
    {
        var store = SignedIn("refresh-0");
        var gateway = Gateway(new Cloud(Grant("refresh-1")), store);
        Assert.NotNull(await gateway.RestoreAsync(CancellationToken.None));

        gateway.SignOut();

        Assert.Null(store.Load());
        Assert.False(gateway.IsSignedIn);
        Assert.Null(await gateway.RestoreAsync(CancellationToken.None));   // not the finished restore's account
    }
}
