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

        public int Grants => _next;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string form = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            if (request.Method != HttpMethod.Post || !form.Contains("grant_type="))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            var (status, body) = grants[Math.Min(_next++, grants.Length - 1)];
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

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")]
    [InlineData(HttpStatusCode.Unauthorized, "")]
    public async Task ARejectedToken_ClearsTheStore(HttpStatusCode status, string body)
    {
        var store = SignedIn("refresh-0");
        var gateway = Gateway(new Cloud((status, body)), store);

        Assert.Null(await gateway.RestoreAsync(CancellationToken.None));
        Assert.Null(store.Load());
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
    }
}
