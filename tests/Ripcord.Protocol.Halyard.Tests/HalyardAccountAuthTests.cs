using System.Web;
using Ripcord.Cloud.Halyard;
using Ripcord.Core.Accounts;
using Ripcord.Core.Platform;
using Ripcord.Core.Security;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// The account sign-in mechanics: the authorize URL we send people to, the redirect we read the result out of,
/// the device id both carry, and what survives a restart.
///
/// <para>
/// None of this talks to the network. What is being pinned is the part that was wrong or missing rather than the
/// part that is hard: the authorize call previously omitted the device identity the token exchange requires, and
/// nothing anywhere read a code out of a redirect at all.
/// </para>
/// </summary>
public class HalyardAccountAuthTests
{
    private static readonly HalyardClientConfig Configured = new(
        ClientId: "test-client",
        ClientSecret: "test-secret",
        RedirectUri: HalyardClientConfig.DefaultRedirectUri,
        Scopes: HalyardClientConfig.DefaultScopes);

    private static HalyardAuthClient Auth(HalyardClientConfig? config = null)
        => new(new HttpClient(), config ?? Configured);

    private static readonly IDeviceIdentity Device =
        new StaticDeviceIdentity(Convert.FromHexString("000102030405060708090a0b0c0d0e0f"));

    // ---- client configuration ------------------------------------------------------------------

    [Theory]
    [InlineData("", "secret", "https://x/y", false)]
    [InlineData("id", "", "https://x/y", false)]
    [InlineData("id", "secret", "", false)]
    [InlineData("  ", "secret", "https://x/y", false)]
    [InlineData("id", "secret", "https://x/y", true)]
    public void IsConfigured_RequiresAllThreeParts(string id, string secret, string redirect, bool expected)
    {
        var config = new HalyardClientConfig(id, secret, redirect, HalyardClientConfig.DefaultScopes);

        Assert.Equal(expected, config.IsConfigured);
    }

    [Fact]
    public void Unconfigured_ReportsItselfAsUnusable()
    {
        // The whole account tier is gated on this, so it is worth a test rather than an assumption.
        Assert.False(HalyardClientConfig.Unconfigured.IsConfigured);
        Assert.Equal(HalyardClientConfig.DefaultRedirectUri, HalyardClientConfig.Unconfigured.RedirectUri);
    }

    // ---- the bundled credential ----------------------------------------------------------------

    [Fact]
    public void BundledCredential_IsEitherAbsentOrWellFormed_NeverHalfPopulated()
    {
        // Shape, never value: asserting the literal credential would copy a secret into a second committed
        // file for no benefit, and would have to be edited in two places on a rotation.
        //
        // Absent is a legitimate outcome and not a failure — it is what -p:BundleOAuthClient=false produces,
        // and what a checkout without the dirty room produces. What must NEVER happen is half-populated: an
        // id with no secret would sail past a null check and fail at the token endpoint with a 401 that says
        // nothing about the real cause.
        if (!HalyardBundledClient.IsBundled)
        {
            Assert.False(HalyardBundledClient.Credential.IsConfigured);
            return;
        }

        HalyardClientConfig bundled = HalyardBundledClient.Credential;

        Assert.True(bundled.IsConfigured);
        Assert.False(string.IsNullOrWhiteSpace(bundled.ClientId));
        Assert.False(string.IsNullOrWhiteSpace(bundled.ClientSecret));
        Assert.Equal(HalyardClientConfig.DefaultRedirectUri, bundled.RedirectUri);
        Assert.Equal(HalyardClientConfig.DefaultScopes, bundled.Scopes);
    }

    [Fact]
    public void BundledCredential_CarriesNoUserOrConsoleMaterial()
    {
        // The line NOTICE draws, enforced rather than promised — the same discipline
        // BundledInteropConstantsTests applies to the v1 constants. This bundle authenticates an
        // application; anything account-scoped belongs in the user's own encrypted account.json.
        string? raw = HalyardBundledClientDiagnostics.ReadRawBundle();
        if (raw is null)
        {
            return; // built with -p:BundleOAuthClient=false
        }

        foreach (string forbidden in (string[])
            ["refreshToken", "refresh_token", "accessToken", "access_token", "accountId", "duid", "npAccountId"])
        {
            Assert.DoesNotContain(forbidden, raw, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task TokenRequest_WithoutACredential_FailsWithAnExplanationRatherThanA401()
    {
        HalyardCloudException ex = await Assert.ThrowsAsync<HalyardCloudException>(
            () => Auth(HalyardClientConfig.Unconfigured).RefreshAsync("some-token", CancellationToken.None));

        Assert.Contains("no oauth client credential", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the authorize URL ---------------------------------------------------------------------

    [Fact]
    public void AuthorizeUrl_CarriesTheDeviceIdentityTheTokenExchangeWillRequire()
    {
        // Omitting these was the difference between a code the token endpoint accepts and one it rejects as
        // having been issued to a different device.
        string duid = HalyardClientDeviceId.For(Device);

        string url = Auth().BuildAuthorizeUrl(duid);

        var q = HttpUtility.ParseQueryString(new Uri(url).Query);
        Assert.Equal(duid, q["duid"]);
        Assert.Equal(HalyardClientConfig.DeviceType, q["device_type"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("test-client", q["client_id"]);
        Assert.Equal(HalyardClientConfig.DefaultRedirectUri, q["redirect_uri"]);
    }

    [Fact]
    public void AuthorizeUrl_RequestsExactlyTheDocumentedScopes()
    {
        string url = Auth().BuildAuthorizeUrl(HalyardClientDeviceId.For(Device));

        string? scope = HttpUtility.ParseQueryString(new Uri(url).Query)["scope"];

        Assert.Equal(string.Join(' ', HalyardClientConfig.DefaultScopes), scope);
    }

    [Fact]
    public void AuthorizeUrl_WithoutADeviceId_Throws()
        => Assert.Throws<ArgumentException>(() => Auth().BuildAuthorizeUrl("  "));

    // ---- reading the code back out -------------------------------------------------------------

    [Fact]
    public void RedirectWithCode_IsRecognised()
    {
        string? code = Auth().TryReadAuthorizationCode(
            new Uri(HalyardClientConfig.DefaultRedirectUri + "?code=abc123&cid=some-uuid"));

        Assert.Equal("abc123", code);
    }

    [Fact]
    public void RedirectMatching_IgnoresExtraQueryParameters()
    {
        // An equality test against the configured redirect never fires, because the real one arrives with
        // parameters beyond `code`. This is the case that makes a naive implementation hang forever.
        Assert.NotNull(Auth().TryReadAuthorizationCode(
            new Uri(HalyardClientConfig.DefaultRedirectUri + "?cid=x&code=abc&extra=1")));
    }

    [Fact]
    public void RedirectMatching_ToleratesATrailingSlash()
        => Assert.Equal("abc", Auth().TryReadAuthorizationCode(
            new Uri(HalyardClientConfig.DefaultRedirectUri + "/?code=abc")));

    [Theory]
    [InlineData("https://my.account.sony.com/signin?code=abc")]        // a different host mid-flow
    [InlineData("https://remoteplay.dl.playstation.net/other?code=abc")] // right host, wrong path
    [InlineData(HalyardClientConfig.DefaultRedirectUri)]                 // the redirect, but no code yet
    [InlineData(HalyardClientConfig.DefaultRedirectUri + "?code=")]      // present but empty
    public void NonCompletionUrls_AreNotMistakenForTheResult(string url)
    {
        // The flow bounces through several intermediate URLs, and stopping on the wrong one means exchanging
        // something that is not an authorization code.
        Assert.Null(Auth().TryReadAuthorizationCode(new Uri(url)));
    }

    // ---- the client device id ------------------------------------------------------------------

    [Fact]
    public void ClientDeviceId_IsThePrefixFollowedByTheMachineId()
    {
        string duid = HalyardClientDeviceId.For(Device);

        Assert.Equal(HalyardClientDeviceId.HexLength, duid.Length);
        Assert.StartsWith(HalyardClientDeviceId.Prefix, duid, StringComparison.Ordinal);
        Assert.EndsWith("000102030405060708090a0b0c0d0e0f", duid, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientDeviceId_IsStableAcrossCalls()
    {
        // A client that presented a different id each launch would accumulate device registrations against the
        // account with nothing to explain why.
        Assert.Equal(HalyardClientDeviceId.For(Device), HalyardClientDeviceId.For(Device));
    }

    [Fact]
    public void ClientDeviceId_WithoutAMachineId_ThrowsRatherThanInventingOne()
    {
        var noIdentity = new StaticDeviceIdentity(default);

        Assert.Throws<InvalidOperationException>(() => HalyardClientDeviceId.For(noIdentity));
    }

    // ---- what survives a restart ---------------------------------------------------------------

    [Fact]
    public void TokenStore_RoundTripsTheRefreshTokenAndCachedIdentity()
    {
        using var dir = new TempDirectory();
        var store = new AccountTokenStore(dir.Paths, new PlaintextCredentialProtector());

        store.Save(new StoredAccountSession("refresh-abc", "4200000000000000042", "somebody", DateTimeOffset.UtcNow));
        StoredAccountSession? loaded = new AccountTokenStore(dir.Paths, new PlaintextCredentialProtector()).Load();

        Assert.NotNull(loaded);
        Assert.Equal("refresh-abc", loaded.RefreshToken);
        Assert.Equal("4200000000000000042", loaded.AccountId);
        Assert.Equal("somebody", loaded.DisplayName);
    }

    [Fact]
    public void TokenStore_DoesNotWriteTheTokenInTheClear()
    {
        // The point of the protector. A plaintext refresh token on disk is a durable account credential any
        // process running as the user can lift.
        using var dir = new TempDirectory();
        var store = new AccountTokenStore(dir.Paths, new Rot13Protector());

        store.Save(new StoredAccountSession("refresh-abc"));

        string raw = File.ReadAllText(Path.Combine(dir.Paths.ConfigDirectory, "account.json"));
        Assert.DoesNotContain("refresh-abc", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void TokenStore_Clear_LeavesNothingBehind()
    {
        // Sign-out. A "signed out" state with a usable token still on disk is a security bug.
        using var dir = new TempDirectory();
        var store = new AccountTokenStore(dir.Paths, new PlaintextCredentialProtector());
        store.Save(new StoredAccountSession("refresh-abc"));

        store.Clear();

        Assert.Null(store.Load());
        Assert.False(File.Exists(Path.Combine(dir.Paths.ConfigDirectory, "account.json")));
    }

    [Fact]
    public void TokenStore_WithAnUndecryptableToken_ReadsAsSignedOutRatherThanThrowing()
    {
        // A file written by a different user or on a different machine. That is "sign in again", not a crash.
        using var dir = new TempDirectory();
        new AccountTokenStore(dir.Paths, new PlaintextCredentialProtector())
            .Save(new StoredAccountSession("refresh-abc"));

        Assert.Null(new AccountTokenStore(dir.Paths, new FailingProtector()).Load());
    }

    [Fact]
    public void TokenStore_WithNoFile_ReadsAsSignedOut()
    {
        using var dir = new TempDirectory();

        Assert.Null(new AccountTokenStore(dir.Paths, new PlaintextCredentialProtector()).Load());
    }

    // ---- helpers -------------------------------------------------------------------------------

    private sealed class TempDirectory : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ripcord-tests", Guid.NewGuid().ToString("n"));

        public TempDirectory() => Directory.CreateDirectory(_root);

        public IPlatformPaths Paths => new FixedPaths(_root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
            }
        }

        private sealed class FixedPaths(string root) : IPlatformPaths
        {
            public string ConfigDirectory => root;

            public string DataDirectory => root;

            public string StateDirectory => root;
        }
    }

    /// <summary>Not encryption — just enough transformation to prove the raw token is not what lands on disk.</summary>
    private sealed class Rot13Protector : ICredentialProtector
    {
        public bool IsRealProtection => true;

        public string Description => "test";

        public byte[] Protect(ReadOnlySpan<byte> plaintext)
        {
            byte[] copy = plaintext.ToArray();
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] ^= 0x5a;
            }

            return copy;
        }

        public byte[]? Unprotect(ReadOnlySpan<byte> ciphertext) => Protect(ciphertext);
    }

    private sealed class FailingProtector : ICredentialProtector
    {
        public bool IsRealProtection => true;

        public string Description => "test";

        public byte[] Protect(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

        public byte[]? Unprotect(ReadOnlySpan<byte> ciphertext) => null;
    }
}
