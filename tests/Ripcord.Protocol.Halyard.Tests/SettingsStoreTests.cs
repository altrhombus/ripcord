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

    // ---- the diagnostics setting's bool-to-rung migration ---------------------------------------

    /// <summary>Write a settings file by hand, the way an older build would have left one.</summary>
    private void WriteSettingsFile(string json)
        => File.WriteAllText(Path.Combine(_dir, "settings.json"), json);

    [Fact]
    public void AFileFromAnOlderBuildKeepsEveryOtherSetting()
    {
        // The failure this migration exists to avoid, and the reason it is not a type change. The store
        // catches JsonException and falls back to defaults, so had ShowDiagnosticsOverlay simply BECOME an
        // enum, this file would not have lost one preference - it would have lost all of them, silently, on
        // the next launch.
        WriteSettingsFile(
            """
            {
              "ShowDiagnosticsOverlay": true,
              "BitrateKbps": 23000,
              "FullScreenOnConnect": false
            }
            """);

        RipcordSettings loaded = new SettingsStore(_paths).Current;

        Assert.Equal(23_000, loaded.BitrateKbps);
        Assert.False(loaded.FullScreenOnConnect);
    }

    [Theory]
    [InlineData("true", DiagnosticsRung.Summary)]
    [InlineData("false", DiagnosticsRung.Hidden)]
    public void AnOlderBuildsAnswerIsHonoured(string legacy, DiagnosticsRung expected)
    {
        WriteSettingsFile($$"""{"ShowDiagnosticsOverlay": {{legacy}}}""");

        RipcordSettings loaded = new SettingsStore(_paths).Current;

        Assert.Null(loaded.DiagnosticsOnConnect);
        Assert.Equal(expected, loaded.DiagnosticsRungOnConnect);
    }

    [Fact]
    public void ThisBuildsAnswerWinsWhenTheFileHasOne()
    {
        // Null means "the file did not say", which is what makes it distinguishable from an answer of
        // Hidden - so a user who deliberately turned the HUD off is not re-migrated from the stale bool.
        WriteSettingsFile(
            """
            {
              "ShowDiagnosticsOverlay": true,
              "DiagnosticsOnConnect": "Hidden"
            }
            """);

        Assert.Equal(DiagnosticsRung.Hidden, new SettingsStore(_paths).Current.DiagnosticsRungOnConnect);
    }

    [Fact]
    public void AnAbsentSettingIsHidden()
    {
        WriteSettingsFile("{}");

        Assert.Equal(DiagnosticsRung.Hidden, new SettingsStore(_paths).Current.DiagnosticsRungOnConnect);
    }

    [Theory]
    [InlineData(DiagnosticsRung.Hidden, false)]
    [InlineData(DiagnosticsRung.Summary, true)]
    [InlineData(DiagnosticsRung.Full, true)]
    public void SavingTheRungKeepsTheLegacyBoolInStep(DiagnosticsRung rung, bool expectedLegacy)
    {
        // Downgrade safety: someone who moves back to an older build should find the HUD where they left it
        // rather than discovering the setting quietly reverted.
        var store = new SettingsStore(_paths);
        store.Save(store.Current.WithDiagnosticsRung(rung));

        RipcordSettings reloaded = new SettingsStore(_paths).Current;

        Assert.Equal(rung, reloaded.DiagnosticsOnConnect);
        Assert.Equal(expectedLegacy, reloaded.ShowDiagnosticsOverlay);
        Assert.Equal(rung, reloaded.DiagnosticsRungOnConnect);
    }

    [Fact]
    public void TheRungPersistsByNameRatherThanByOrdinal()
    {
        // Same reason every other enum here does: reordering or extending the enum must not silently change
        // what an already-saved file means.
        var store = new SettingsStore(_paths);
        store.Save(store.Current.WithDiagnosticsRung(DiagnosticsRung.Full));

        string json = File.ReadAllText(Path.Combine(_dir, "settings.json"));

        Assert.Contains("\"Full\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void TheComputedRungIsNotItselfWrittenToTheFile()
    {
        // It is derived from two other keys. Writing it would put a third answer to the same question in the
        // file, and a file that can disagree with itself.
        var store = new SettingsStore(_paths);
        store.Save(store.Current.WithDiagnosticsRung(DiagnosticsRung.Summary));

        string json = File.ReadAllText(Path.Combine(_dir, "settings.json"));

        Assert.DoesNotContain(nameof(RipcordSettings.DiagnosticsRungOnConnect), json, StringComparison.Ordinal);
    }
}
