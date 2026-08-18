using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ripcord.Core.Platform;
using Ripcord.Core.Security;

namespace Ripcord.Core.Accounts;

/// <summary>
/// File-backed account persistence: a JSON file in the platform config directory with the refresh token
/// encrypted at rest via <see cref="ICredentialProtector"/> (DPAPI on Windows).
///
/// <para>
/// The structural twin of <see cref="Consoles.PairedConsoleStore"/> next door, down to the atomic temp-file write
/// and the fall-back-rather-than-throw read, and for the same reasons. It is a separate file from
/// <c>consoles.json</c> rather than a field inside it because the two have different lifetimes: signing out must
/// not disturb pairings, and forgetting a console must not sign you out.
/// </para>
/// </summary>
public sealed partial class AccountTokenStore : IAccountTokenStore
{
    /// <summary>
    /// The persisted shape. Separate from <see cref="StoredAccountSession"/> because the token on disk is
    /// ciphertext and the token in memory is not — collapsing the two is how a plaintext credential ends up in a
    /// file that everyone assumed was encrypted.
    /// </summary>
    private sealed record PersistedAccount(
        [property: JsonPropertyName("refreshToken")] string ProtectedRefreshToken,
        [property: JsonPropertyName("accountId")] string? AccountId,
        [property: JsonPropertyName("displayName")] string? DisplayName,
        [property: JsonPropertyName("savedAt")] DateTimeOffset SavedAt);

    // Source-generated: reflection-based JSON is unreadable under trimming, and an install whose account file
    // silently failed to deserialize would present a signed-in user with a sign-in prompt on every launch.
    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(PersistedAccount))]
    private partial class AccountContext : JsonSerializerContext;

    private readonly string _path;
    private readonly ICredentialProtector _protector;

    public AccountTokenStore(IPlatformPaths? paths = null, ICredentialProtector? protector = null)
    {
        IPlatformPaths resolved = paths ?? new DefaultPlatformPaths();
        _path = Path.Combine(resolved.ConfigDirectory, "account.json");
        _protector = protector ?? CredentialProtection.ForCurrentPlatform();
    }

    public string ProtectionDescription => _protector.Description;

    public bool TokenEncrypted => _protector.IsRealProtection;

    public StoredAccountSession? Load()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            PersistedAccount? persisted = JsonSerializer.Deserialize(
                File.ReadAllText(_path), AccountContext.Default.PersistedAccount);
            if (persisted is null)
            {
                return null;
            }

            // An undecryptable token means a different user or machine, or a file written before protection was
            // available. That is "signed out", not "crash" — the next sign-in overwrites it.
            string? token = Unwrap(persisted.ProtectedRefreshToken);
            return token is null
                ? null
                : new StoredAccountSession(token, persisted.AccountId, persisted.DisplayName, persisted.SavedAt);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Save(StoredAccountSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var persisted = new PersistedAccount(
            Wrap(session.RefreshToken),
            session.AccountId,
            session.DisplayName,
            session.SavedAt == default ? DateTimeOffset.UtcNow : session.SavedAt);

        // Temp file + move, so an interrupted write cannot leave a truncated file that signs the user out.
        string tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(persisted, AccountContext.Default.PersistedAccount));
        File.Move(tmp, _path, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Sign-out must not throw. If the file cannot be removed the token is still on disk, which is worth
            // knowing about — but the caller's own state has already been cleared, so the next Load that does
            // succeed will be overwritten by the next sign-in.
        }
    }

    private string Wrap(string refreshToken)
        => Consoles.PairedConsoleBlob.ProtectedPrefix
           + Convert.ToBase64String(_protector.Protect(Encoding.UTF8.GetBytes(refreshToken)));

    private string? Unwrap(string stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        // No legacy plaintext path here, unlike the console store: this file has never existed in an
        // unencrypted form, so anything without the prefix is corrupt rather than old.
        if (!stored.StartsWith(Consoles.PairedConsoleBlob.ProtectedPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            byte[]? plain = _protector.Unprotect(
                Convert.FromBase64String(stored[Consoles.PairedConsoleBlob.ProtectedPrefix.Length..]));
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>A non-persistent store, for tests and for hosts that decline to keep a token on disk.</summary>
public sealed class InMemoryAccountTokenStore : IAccountTokenStore
{
    private StoredAccountSession? _session;

    public string ProtectionDescription => "held in memory only (not persisted)";

    public bool TokenEncrypted => false;

    public StoredAccountSession? Load() => _session;

    public void Save(StoredAccountSession session) => _session = session;

    public void Clear() => _session = null;
}
