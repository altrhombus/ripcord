using System.Text.Json;
using System.Text.Json.Serialization;
using Ripcord.Core.Platform;

namespace Ripcord.Cloud.Halyard;

/// <summary>
/// Where the OAuth client credential comes from at runtime.
///
/// <para>
/// Three sources, in strict precedence: the environment, then a <c>client.json</c> in the platform config
/// directory, then whatever this build bundled (<see cref="HalyardBundledClient"/>). The environment is for the
/// console harness and a developer shell and wins outright, so a variable set for one command overrides
/// everything. The file is for an installed app — it is how a user with their own registered client points the
/// shipped binary at it without rebuilding. The bundle is the fallback that makes sign-in work out of the box.
/// </para>
///
/// <para>
/// Absent all three, the result is <see cref="HalyardClientConfig.Unconfigured"/>, which reports
/// <see cref="HalyardClientConfig.IsConfigured"/> false and makes every account surface say so. That is what a
/// <c>-p:BundleOAuthClient=false</c> build gets, and it is not a failure state — LAN play needs none of it.
/// </para>
///
/// <para>
/// <b><c>client.json</c> holds a secret and is never committed</b> (it is in <c>.gitignore</c>). It is not
/// encrypted at rest, unlike the refresh token next door in <c>account.json</c>: this one authenticates an
/// application rather than a person, and the bundled copy is public anyway, so encrypting it would be
/// theatre.
/// </para>
/// </summary>
public static partial class HalyardClientConfigFile
{
    public const string FileName = "client.json";

    private sealed record PersistedClientConfig(
        [property: JsonPropertyName("clientId")] string? ClientId,
        [property: JsonPropertyName("clientSecret")] string? ClientSecret,
        [property: JsonPropertyName("redirectUri")] string? RedirectUri);

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(PersistedClientConfig))]
    private partial class ClientConfigContext : JsonSerializerContext;

    /// <summary>
    /// Load the credential: environment, then <c>client.json</c>, then whatever this build bundled, then
    /// nothing.
    ///
    /// <para>
    /// The bundled credential is deliberately <em>last</em>. Anyone who has their own registered client — or
    /// who is testing against a different one — must be able to override what shipped without rebuilding, and
    /// a bundle that won the lookup would make that impossible.
    /// </para>
    /// </summary>
    public static HalyardClientConfig Load(IPlatformPaths? paths = null)
    {
        HalyardClientConfig fromEnvironment = HalyardClientConfig.FromEnvironment();
        if (fromEnvironment.IsConfigured)
        {
            return fromEnvironment;
        }

        IPlatformPaths resolved = paths ?? new DefaultPlatformPaths();
        string path = Path.Combine(resolved.ConfigDirectory, FileName);
        if (!File.Exists(path))
        {
            return HalyardBundledClient.Credential;
        }

        try
        {
            PersistedClientConfig? persisted = JsonSerializer.Deserialize(
                File.ReadAllText(path), ClientConfigContext.Default.PersistedClientConfig);
            if (persisted is null)
            {
                return HalyardClientConfig.Unconfigured;
            }

            return new HalyardClientConfig(
                ClientId: persisted.ClientId ?? string.Empty,
                ClientSecret: persisted.ClientSecret ?? string.Empty,
                RedirectUri: string.IsNullOrWhiteSpace(persisted.RedirectUri)
                    ? HalyardClientConfig.DefaultRedirectUri
                    : persisted.RedirectUri,
                Scopes: HalyardClientConfig.DefaultScopes);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A malformed credential file means "not configured", which every surface already renders. Throwing
            // here would take down startup over a file the user may not know exists.
            return HalyardClientConfig.Unconfigured;
        }
    }

    /// <summary>
    /// Write a credential, for a settings surface that lets the user supply one. Returns the path written, so
    /// the surface can tell the user where it went.
    /// </summary>
    public static string Save(HalyardClientConfig config, IPlatformPaths? paths = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        IPlatformPaths resolved = paths ?? new DefaultPlatformPaths();
        string path = Path.Combine(resolved.ConfigDirectory, FileName);
        var persisted = new PersistedClientConfig(config.ClientId, config.ClientSecret, config.RedirectUri);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(persisted, ClientConfigContext.Default.PersistedClientConfig));
        File.Move(tmp, path, overwrite: true);
        return path;
    }
}
