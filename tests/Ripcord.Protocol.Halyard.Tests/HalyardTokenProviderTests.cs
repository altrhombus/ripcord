using System.Net;
using System.Text;
using Ripcord.Cloud.Halyard;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The token provider's two faults the Mac port found (2026-09-26): concurrent callers each spent the rotating
/// refresh token, and sign-out left the provider seeded.
/// </summary>
public class HalyardTokenProviderTests
{
    /// <summary>A token endpoint that counts refresh grants and answers each after a gate opens.</summary>
    private sealed class TokenEndpoint : HttpMessageHandler
    {
        private int _grants;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Grants => Volatile.Read(ref _grants);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int n = Interlocked.Increment(ref _grants);
            await Release.Task.WaitAsync(cancellationToken);
            string body = $"{{\"access_token\":\"access-{n}\",\"refresh_token\":\"refresh-{n}\",\"token_type\":\"bearer\",\"expires_in\":3600,\"scope\":\"x\"}}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly HalyardClientConfig Config = new("id", "secret", HalyardClientConfig.DefaultRedirectUri, HalyardClientConfig.DefaultScopes);
    private static readonly HalyardTokens Expired = new("old-access", "refresh-0", DateTimeOffset.UtcNow.AddHours(-1));

    [Fact]
    public async Task ConcurrentCallers_ShareOneRefresh()
    {
        var endpoint = new TokenEndpoint();
        var provider = new HalyardTokenProvider(new HalyardAuthClient(new HttpClient(endpoint), Config));
        provider.Seed(Expired);

        Task<string>[] callers = [.. Enumerable.Range(0, 5).Select(_ => provider.GetAccessTokenAsync(CancellationToken.None))];
        await Task.Delay(50);
        endpoint.Release.SetResult();
        string[] tokens = await Task.WhenAll(callers);

        Assert.Equal(1, endpoint.Grants);
        Assert.All(tokens, t => Assert.Equal("access-1", t));
        Assert.Equal("refresh-1", provider.Current!.RefreshToken);
    }

    [Fact]
    public async Task ACallerThatStopsWaiting_DoesNotCancelTheOthers()
    {
        var endpoint = new TokenEndpoint();
        var provider = new HalyardTokenProvider(new HalyardAuthClient(new HttpClient(endpoint), Config));
        provider.Seed(Expired);

        using var impatient = new CancellationTokenSource();
        Task<string> first = provider.GetAccessTokenAsync(impatient.Token);
        Task<string> second = provider.GetAccessTokenAsync(CancellationToken.None);
        await impatient.CancelAsync();
        endpoint.Release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("access-1", await second);
    }

    [Fact]
    public async Task Clear_SignsTheProviderOut_AndARefreshInFlightDoesNotSignItBackIn()
    {
        var endpoint = new TokenEndpoint();
        var provider = new HalyardTokenProvider(new HalyardAuthClient(new HttpClient(endpoint), Config));
        provider.Seed(Expired);

        Task<string> inFlight = provider.GetAccessTokenAsync(CancellationToken.None);
        await Task.Delay(50);
        provider.Clear();
        endpoint.Release.SetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => inFlight);
        Assert.Null(provider.Current);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync(CancellationToken.None));
    }
}
