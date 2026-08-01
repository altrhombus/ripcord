using Ripcord.Core.Platform;
using Ripcord.Core.Sessions;
using Ripcord.Core.Input;
using Ripcord.Core.Settings;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Settings persistence. The behaviours that matter are the failure modes: a corrupt file must not stop the app
/// starting, and an interrupted write must not lose everything.
/// </summary>
public class SettingsStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly IPlatformPaths _paths;

    public SettingsStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ripcord-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _paths = new FixedPaths(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class FixedPaths(string dir) : IPlatformPaths
    {
        public string ConfigDirectory { get; } = dir;
        public string DataDirectory { get; } = dir;
        public string StateDirectory { get; } = dir;
    }

    [Fact]
    public void InputBindingsSurviveARoundTrip()
    {
        // Worth its own test because of what has to survive: a Dictionary<int, InputAction> (non-string keys), a
        // Dictionary<ControllerButtons, ControllerButtons> (enum keys), and enum VALUES persisted by name so a
        // reordered or extended enum cannot silently change what a saved file means. A remap that did not survive
        // a restart would defeat the entire purpose of being able to remap exotic hardware.
        var store = new SettingsStore(_paths);

        InputBindings bindings = new InputBindings
        {
            KeyboardEnabled = true,
            GamepadRemap = new Dictionary<ControllerButtons, ControllerButtons>
            {
                [ControllerButtons.South] = ControllerButtons.East,
            },
        }.WithKey(VirtualKeys.Number1, InputAction.North);

        store.Save(store.Current with { InputBindings = bindings });

        InputBindings loaded = new SettingsStore(_paths).Current.InputBindings;

        Assert.True(loaded.KeyboardEnabled);
        Assert.Equal(InputAction.North, loaded.Keyboard[VirtualKeys.Number1]);
        Assert.Equal(InputAction.LeftStickUp, loaded.Keyboard[VirtualKeys.W]);  // untouched default survives
        Assert.Equal(ControllerButtons.East, loaded.GamepadRemap[ControllerButtons.South]);
    }

    [Fact]
    public void DefaultsAreTheVerifiedGoodConfiguration()
    {
        RipcordSettings s = new SettingsStore(_paths).Current;

        Assert.Equal(1280, s.Width);
        Assert.Equal(720, s.Height);
        Assert.Equal(60, s.TargetFps);
        Assert.True(s.AdaptiveQuality);
        Assert.Equal(GpuPreference.Auto, s.GpuPreference);

        // The overlay used to be on by default, printing stick coordinates over the game.
        Assert.False(s.ShowDiagnosticsOverlay);

        // There must always be a way out of a stream with a controller.
        Assert.NotEqual(ExitGesture.None, s.ExitGesture);

        // Unvalidated protocol writes must be opt-in, never on by default.
        Assert.False(s.ReportConnectionQuality);
    }

    [Fact]
    public void SaveThenReload_RoundTripsEveryField()
    {
        var store = new SettingsStore(_paths);
        var updated = store.Current with
        {
            Width = 1920,
            Height = 1080,
            TargetFps = 30,
            BitrateKbps = 25_000,
            Codec = VideoCodec.Hevc,
            LatencyMode = LatencyMode.Lowest,
            UpscaleMode = UpscaleMode.FsrFallback,
            AdaptiveQuality = false,
            ReportConnectionQuality = true,
            GpuPreference = GpuPreference.Specific,
            GpuLuid = 0xDEADBEEFUL,
            ExitGesture = ExitGesture.BothSticksClicked,
            UiStickDeadzone = 0.35,
            FullScreenOnConnect = false,
            ShowDiagnosticsOverlay = true,
            LargeUiScale = true,
        };

        store.Save(updated);

        Assert.Equal(updated, new SettingsStore(_paths).Current);
    }

    [Fact]
    public void EnumsPersistByName_SoReorderingThemCannotChangeMeaning()
    {
        var store = new SettingsStore(_paths);
        store.Save(store.Current with { Codec = VideoCodec.Hevc, GpuPreference = GpuPreference.PreferEfficiency });

        string json = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.Contains("Hevc", json);
        Assert.Contains("PreferEfficiency", json);
    }

    [Fact]
    public void CorruptFile_FallsBackToDefaultsRatherThanThrowing()
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ this is not json");

        RipcordSettings s = new SettingsStore(_paths).Current;
        Assert.Equal(new RipcordSettings(), s);
    }

    [Fact]
    public void PartiallyUnknownFile_KeepsWhatItCanAndDefaultsTheRest()
    {
        // A file written by a newer or older build must not be all-or-nothing.
        File.WriteAllText(
            Path.Combine(_dir, "settings.json"),
            """{ "Width": 1920, "Height": 1080, "SomeFutureSetting": 42 }""");

        RipcordSettings s = new SettingsStore(_paths).Current;
        Assert.Equal(1920, s.Width);
        Assert.Equal(1080, s.Height);
        Assert.Equal(60, s.TargetFps); // untouched by the file, so still the default
    }

    [Fact]
    public void Save_NotifiesObservers()
    {
        var store = new SettingsStore(_paths);
        RipcordSettings? seen = null;
        store.Changed += s => seen = s;

        store.Save(store.Current with { BitrateKbps = 15_000 });

        Assert.NotNull(seen);
        Assert.Equal(15_000, seen!.BitrateKbps);
    }

    [Fact]
    public void ToSessionConfig_ReflectsTheSettings()
    {
        var s = new RipcordSettings { Width = 1920, Height = 1080, TargetFps = 30, BitrateKbps = 20_000 };
        SessionConfig c = s.ToSessionConfig();

        Assert.Equal(1920, c.Width);
        Assert.Equal(1080, c.Height);
        Assert.Equal(30, c.TargetFps);
        Assert.Equal(20_000, c.InitialBitrateKbps);
    }

    [Fact]
    public void NoLeftoverTempFileAfterSave()
    {
        // Writes go temp-then-move; a stray .tmp would mean the move did not happen.
        var store = new SettingsStore(_paths);
        store.Save(store.Current with { BitrateKbps = 12_000 });

        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        Assert.True(File.Exists(Path.Combine(_dir, "settings.json")));
    }
}
