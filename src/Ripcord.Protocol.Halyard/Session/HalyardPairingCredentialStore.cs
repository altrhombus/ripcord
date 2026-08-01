using System.Text.Json;
using Ripcord.Core.Discovery;
using Ripcord.Core.Platform;
using Ripcord.Core.Security;

namespace Ripcord.Protocol.Halyard.Session;

/// <summary>
/// A file-backed <see cref="IConsoleCredentialStore"/>: a JSON map of <c>consoleId → blob(hex)</c>, where the
/// blob is a serialized <see cref="Common.Crypto.HalyardPairingRecord"/> (registkey + companion + keytype).
/// This is the concrete backing the session layer's credential seam has lacked — the value
/// <see cref="LoadAsync"/> returns is exactly what <c>HalyardStreamingSession</c> deserializes to drive
/// <c>/sess/init</c> + the control KDF.
///
/// <para>
/// The blob is a long-lived credential that authenticates this client to the console, so it is encrypted at
/// rest through <see cref="ICredentialProtector"/> (DPAPI on Windows) rather than stored as bare hex. Records
/// written before protection existed are still readable: <see cref="LoadAsync"/> falls back to interpreting an
/// entry as plaintext hex, and the next save rewrites it protected.
/// </para>
/// <para>
/// The path is injectable so tests use a temp file; <see cref="ForCurrentUser"/> is the app default
/// (<c>credentials.json</c> under <see cref="IPlatformPaths.ConfigDirectory"/>).
/// </para>
/// </summary>
public sealed class HalyardPairingCredentialStore : IConsoleCredentialStore
{
    private readonly string _path;
    private readonly ICredentialProtector _protector;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    /// <summary>Marks an entry as ciphertext, so a legacy plaintext-hex entry is still distinguishable.</summary>
    private const string ProtectedPrefix = "dpapi:";

    public HalyardPairingCredentialStore(string path, ICredentialProtector? protector = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        _protector = protector ?? CredentialProtection.ForCurrentPlatform();
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>How the credentials are protected at rest, for diagnostics and the settings UI.</summary>
    public string ProtectionDescription => _protector.Description;

    /// <summary>The per-user default store (<c>credentials.json</c> in the platform config directory).</summary>
    public static HalyardPairingCredentialStore ForCurrentUser(
        IPlatformPaths? paths = null, ICredentialProtector? protector = null)
    {
        IPlatformPaths resolved = paths ?? new DefaultPlatformPaths();
        return new HalyardPairingCredentialStore(Path.Combine(resolved.ConfigDirectory, "credentials.json"), protector);
    }

    public async Task SaveAsync(string consoleId, byte[] registrationKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(consoleId);
        ArgumentNullException.ThrowIfNull(registrationKey);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> map = await ReadAsync(cancellationToken).ConfigureAwait(false);
            map[consoleId] = ProtectedPrefix + Convert.ToBase64String(_protector.Protect(registrationKey));
            await WriteAsync(map, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<byte[]?> LoadAsync(string consoleId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(consoleId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> map = await ReadAsync(cancellationToken).ConfigureAwait(false);
            return map.TryGetValue(consoleId, out string? stored) && stored is not null
                ? Decode(stored)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string consoleId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(consoleId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Dictionary<string, string> map = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (map.Remove(consoleId))
                await WriteAsync(map, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Decode a stored entry. Protected entries carry a prefix; anything else is treated as a legacy plaintext
    /// hex blob so an existing pairing survives the upgrade rather than silently forcing a re-pair. A protected
    /// entry that will not decrypt (different user/machine, corrupt file) returns null — the caller treats that
    /// as "not paired", which is the correct, recoverable outcome.
    /// </summary>
    private byte[]? Decode(string stored)
    {
        try
        {
            if (stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            {
                return _protector.Unprotect(Convert.FromBase64String(stored[ProtectedPrefix.Length..]));
            }

            return Convert.FromHexString(stored);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task<Dictionary<string, string>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
            return new(StringComparer.Ordinal);
        try
        {
            await using FileStream fs = File.OpenRead(_path);
            var map = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(fs, cancellationToken: cancellationToken).ConfigureAwait(false);
            return map ?? new(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // A corrupt file must not brick pairing; treat as empty (the next Save rewrites it).
            return new(StringComparer.Ordinal);
        }
    }

    private async Task WriteAsync(Dictionary<string, string> map, CancellationToken cancellationToken)
    {
        // Write to a temp file and move into place so a crash mid-write can't corrupt the store.
        string tmp = _path + ".tmp";
        await using (FileStream fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, map, JsonOpts, cancellationToken).ConfigureAwait(false);
        }
        File.Move(tmp, _path, overwrite: true);
    }
}
