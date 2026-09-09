using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ripcord.Cloud.Halyard;

/// <summary>
/// The OAuth client credential bundled with this build, if any.
///
/// <para>
/// <b>What this is, stated plainly.</b> It is the credential the vendor's own desktop client authenticates
/// with, recovered from this project's own capture of its own traffic and independently verified to be already
/// published elsewhere. PlayStation Network offers no third-party client registration, so there is no credential
/// of "ours" to obtain: without this one, the account tier is unreachable by any client that is not Sony's.
/// </para>
///
/// <para>
/// <b>How it differs from the v1 interoperability constants</b>, because the distinction is worth keeping
/// sharp rather than blurring the two into one exception. Those are wire-protocol facts — the console computes
/// against them, and a client cannot speak the protocol at all without them. This is an <em>access
/// credential</em> for a network service, and a client demonstrably speaks the protocol without it: a LAN
/// session works against a console with no internet connection. It is bundled because it is the only way to
/// reach the account tier, not because the protocol requires it, and <c>NOTICE</c> makes that argument
/// separately for exactly that reason.
/// </para>
///
/// <para>
/// It is generic: identical for every user and every console, tied to no account, and authenticating an
/// application rather than a person. No user credential is bundled with anything — the signed-in account's
/// tokens are generated per user and stay on that user's machine.
/// </para>
///
/// <para>
/// Absent when built with <c>-p:BundleOAuthClient=false</c>, or when the placeholder has never been populated
/// (a checkout without the dirty room). Both cases report <see cref="IsBundled"/> false and degrade to
/// "sign-in is unavailable", which every account surface already renders.
/// </para>
/// </summary>
public static partial class HalyardBundledClient
{
    private sealed record BundledCredential(
        [property: JsonPropertyName("clientId")] string? ClientId,
        [property: JsonPropertyName("clientSecret")] string? ClientSecret);

    [JsonSerializable(typeof(BundledCredential))]
    private partial class BundledClientContext : JsonSerializerContext;

    private const string ResourceName = "Ripcord.Cloud.Halyard.Data.halyard-oauth-client.json";

    // Read once. The resource cannot change for the lifetime of the process, and a sign-in that had to
    // re-parse it would be paying for nothing.
    private static readonly HalyardClientConfig Resolved = Read();

    /// <summary>Whether this build carries a usable credential.</summary>
    public static bool IsBundled => Resolved.IsConfigured;

    /// <summary>
    /// The bundled credential, or an unconfigured one. Never null, so callers chain it as the last resort
    /// without a branch.
    /// </summary>
    public static HalyardClientConfig Credential => Resolved;

    private static HalyardClientConfig Read()
    {
        try
        {
            using Stream? stream = typeof(HalyardBundledClient).Assembly
                .GetManifestResourceStream(ResourceName);
            if (stream is null)
            {
                return HalyardClientConfig.Unconfigured; // -p:BundleOAuthClient=false
            }

            BundledCredential? parsed = JsonSerializer.Deserialize(
                stream, BundledClientContext.Default.BundledCredential);

            // The shipped file is populated, so this branch is not the normal path -- it is what a build made
            // with -p:BundleOAuthClient=false, or a deliberately emptied file, produces. Either is
            // indistinguishable here from an omitted bundle, and all three mean the same thing to every caller.
            return string.IsNullOrWhiteSpace(parsed?.ClientId) || string.IsNullOrWhiteSpace(parsed.ClientSecret)
                ? HalyardClientConfig.Unconfigured
                : new HalyardClientConfig(
                    parsed.ClientId,
                    parsed.ClientSecret,
                    HalyardClientConfig.DefaultRedirectUri,
                    HalyardClientConfig.DefaultScopes);
        }
        catch (Exception ex) when (ex is JsonException or IOException or BadImageFormatException)
        {
            // A corrupt resource means "no credential", which is a state the whole app already handles.
            // Throwing here would take down startup over something the user cannot act on.
            return HalyardClientConfig.Unconfigured;
        }
    }
}

/// <summary>
/// A convenience for reflecting over what this build actually shipped, used by the tests that guard the
/// bundle. Separate from <see cref="HalyardBundledClient"/> so the guard cannot be satisfied by the same
/// code path it is checking.
/// </summary>
public static class HalyardBundledClientDiagnostics
{
    /// <summary>The raw bundled JSON, or null when the build omitted it.</summary>
    public static string? ReadRawBundle()
    {
        using Stream? stream = typeof(HalyardBundledClient).Assembly
            .GetManifestResourceStream("Ripcord.Cloud.Halyard.Data.halyard-oauth-client.json");
        if (stream is null)
        {
            return null;
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
