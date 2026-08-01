using System.Text.Json;
using System.Text.Json.Serialization;
using Ripcord.Core.Platform;

namespace Ripcord.Core.Settings;

/// <summary>Reads and writes the user's settings.</summary>
public interface ISettingsStore
{
    /// <summary>The current settings; defaults when nothing has been saved.</summary>
    RipcordSettings Current { get; }

    /// <summary>Persist and publish <paramref name="settings"/>.</summary>
    void Save(RipcordSettings settings);

    /// <summary>Raised after a successful <see cref="Save"/>.</summary>
    event Action<RipcordSettings>? Changed;
}

/// <summary>
/// JSON-file settings store under <see cref="IPlatformPaths.ConfigDirectory"/>.
///
/// <para>
/// Two deliberate robustness choices, both learned from the credential store next door: writes go to a temp
/// file and are moved into place, so an interrupted write cannot leave a truncated file that loses every
/// setting; and an unreadable or partially-corrupt file falls back to defaults rather than throwing, because
/// bad settings must never prevent the app from starting.
/// </para>
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Persist enums by name: a reordered or extended enum must not silently change what a saved file means.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly Lock _gate = new();
    private RipcordSettings _current;

    public SettingsStore(IPlatformPaths? paths = null, string fileName = "settings.json")
    {
        IPlatformPaths resolved = paths ?? new DefaultPlatformPaths();
        _path = Path.Combine(resolved.ConfigDirectory, fileName);
        _current = Load();
    }

    public RipcordSettings Current
    {
        get { lock (_gate) return _current; }
    }

    public event Action<RipcordSettings>? Changed;

    public void Save(RipcordSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_gate)
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tmp, _path, overwrite: true);
            _current = settings;
        }

        Changed?.Invoke(settings);
    }

    private RipcordSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new RipcordSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<RipcordSettings>(File.ReadAllText(_path), JsonOptions)
                   ?? new RipcordSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable settings must not block startup; the next save rewrites the file.
            return new RipcordSettings();
        }
    }
}

/// <summary>In-memory store for tests and for hosts that do not persist.</summary>
public sealed class InMemorySettingsStore(RipcordSettings? initial = null) : ISettingsStore
{
    public RipcordSettings Current { get; private set; } = initial ?? new RipcordSettings();

    public event Action<RipcordSettings>? Changed;

    public void Save(RipcordSettings settings)
    {
        Current = settings;
        Changed?.Invoke(settings);
    }
}
