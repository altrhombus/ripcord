using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using Ripcord.Core.Platform;
using Ripcord.Media.Interop;
using Windows.ApplicationModel.DataTransfer;

namespace Ripcord_App.Pages;

/// <summary>
/// Identity, provenance, and environment facts. The device table is deliberately the things you would otherwise
/// ask a user to go and find when diagnosing a report: Windows build, runtime, GPU, whether hardware decoding is
/// available, and where the config lives — with a button that puts the lot on the clipboard, because a report
/// that has to be assembled by hand usually arrives without half of it.
/// </summary>
public sealed partial class AboutPage : Page
{
    // Segoe Fluent Icons: CheckMark, Warning, Remove. Escaped rather than pasted so the file stays plain ASCII.
    private const string CheckGlyph = "\uE73E";
    private const string WarningGlyph = "\uE7BA";
    private const string AbsentGlyph = "\uE738";

    /// <summary>
    /// The rendered device facts, in display order, kept so "Copy details" reproduces exactly what is on screen
    /// rather than re-deriving it (and re-running the GPU probe).
    /// </summary>
    private readonly List<(string Label, string Value)> _details = [];

    public AboutPage()
    {
        InitializeComponent();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        _details.Clear();

        VersionText.Text = $"Version {ResolveVersion()}";
        Record("Ripcord", ResolveVersion());

        WindowsText.Text = DescribeWindows();
        Record("Windows", WindowsText.Text);

        // Lowercased: the enum renders "X64", and every other place a user meets this — the build output, the
        // publish folder, Task Manager — spells it "x64".
        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        RuntimeText.Text = $".NET {Environment.Version} · {architecture}";
        Record("Runtime", RuntimeText.Text);

        // Seeded in display order so the copied text matches the table even though these three arrive later.
        Record("Graphics", "checking");
        Record("Video decoding", "checking");
        Record("HEVC", "checking");

        try
        {
            ConfigPathText.Text = new DefaultPlatformPaths().ConfigDirectory;
        }
        catch (Exception ex)
        {
            // Nothing here is worth taking the page down for — the folder is a convenience, not the point.
            Debug.WriteLine($"[Ripcord] config path unavailable: {ex.Message}");
            ConfigPathText.Text = "unavailable";
            OpenConfigButton.IsEnabled = false;
        }

        Record("Settings folder", ConfigPathText.Text);

        // The graphics probes create and destroy a D3D12 device per adapter, which is far too much work to do on
        // the UI thread while the page is being shown. Those rows read "Checking…" until this lands.
        _ = ProbeGraphicsAsync();
    }

    /// <summary>
    /// Fill the Graphics, Video decoding and HEVC rows from the native capability queries, off the UI thread.
    /// </summary>
    private async Task ProbeGraphicsAsync()
    {
        GraphicsProbe probe = await Task.Run(RunGraphicsProbe);

        GraphicsText.Text = probe.Graphics;
        Record("Graphics", probe.Graphics);

        if (probe.Failure is { } failure)
        {
            // A failed probe is itself a diagnostic, so say so rather than quietly reporting "not available".
            SetPill(DecodePill, DecodePillIcon, DecodePillText, PillTone.Caution, "Couldn't check");
            DecodeNoteText.Text = failure;
            DecodeNoteText.Visibility = Visibility.Visible;
            SetPill(HevcPill, HevcPillIcon, HevcPillText, PillTone.Caution, "Couldn't check");
            Record("Video decoding", $"could not be checked — {failure}");
            Record("HEVC", "could not be checked");
            return;
        }

        if (probe.HardwareDecode)
        {
            SetPill(DecodePill, DecodePillIcon, DecodePillText, PillTone.Success, "Hardware accelerated");
            Record("Video decoding", "hardware accelerated");
        }
        else
        {
            SetPill(DecodePill, DecodePillIcon, DecodePillText, PillTone.Caution, "Software fallback");
            DecodeNoteText.Text = "This GPU doesn't report H.264 decode support, so streaming may fall back to the "
                                  + "CPU and cost more power.";
            DecodeNoteText.Visibility = Visibility.Visible;
            Record("Video decoding", "no hardware support reported");
        }

        if (probe.Hevc)
        {
            SetPill(HevcPill, HevcPillIcon, HevcPillText, PillTone.Success, "Available");
            Record("HEVC", "available");
        }
        else
        {
            // Not a fault: HEVC is an option and H.264 is the default, so this is neutral, not a caution.
            SetPill(HevcPill, HevcPillIcon, HevcPillText, PillTone.Neutral, "Not installed");
            Record("HEVC", "not installed");
        }
    }

    private readonly record struct GraphicsProbe(string Graphics, bool HardwareDecode, bool Hevc, string? Failure);

    private static GraphicsProbe RunGraphicsProbe()
    {
        try
        {
            IReadOnlyList<VideoAdapterInfo> adapters = VideoCapabilities.EnumerateAdapters();
            return new GraphicsProbe(
                DescribeAdapters(adapters),
                VideoCapabilities.IsD3D12VideoDecodeSupported(),
                VideoCapabilities.IsCodecDecodeAvailable(VideoCodecKind.Hevc),
                Failure: null);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Ripcord] graphics probe failed: {ex.Message}");
            return new GraphicsProbe("unavailable", false, false, ex.Message);
        }
    }

    /// <summary>
    /// Name the adapter a stream would actually run on — the decode-capable one driving a display, which is what
    /// the Automatic GPU preference resolves to — and count the others rather than listing every adapter equally.
    /// </summary>
    private static string DescribeAdapters(IReadOnlyList<VideoAdapterInfo> adapters)
    {
        if (adapters.Count == 0)
        {
            return "no adapters reported";
        }

        int chosen = IndexOfFirst(adapters, a => a.DrivesADisplay && a.SupportsHardwareDecode);
        if (chosen < 0)
        {
            chosen = IndexOfFirst(adapters, a => a.DrivesADisplay);
        }

        VideoAdapterInfo adapter = adapters[chosen < 0 ? 0 : chosen];

        // Deliberately not reporting DedicatedVideoMemory: on the integrated GPU most of these machines stream
        // from, it is a carve-out of system RAM and reads as "this GPU has half a gigabyte", which is both
        // alarming and irrelevant to whether a stream will decode.
        var text = new StringBuilder(adapter.Description);

        int others = adapters.Count - 1;
        if (others > 0)
        {
            text.Append(others == 1 ? " · 1 other adapter installed" : $" · {others} other adapters installed");
        }

        return text.ToString();
    }

    private static int IndexOfFirst(IReadOnlyList<VideoAdapterInfo> adapters, Func<VideoAdapterInfo, bool> match)
    {
        for (int i = 0; i < adapters.Count; i++)
        {
            if (match(adapters[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// A Windows version string worth pasting into a bug report: the marketing version (25H2) and the full build
    /// with its update revision, neither of which <see cref="Environment.OSVersion"/> gives on its own — it
    /// reports the build but leaves the revision at zero.
    /// </summary>
    private static string DescribeWindows()
    {
        Version version = Environment.OSVersion.Version;
        string name = version.Build >= 22000 ? "Windows 11" : "Windows 10";
        string build = version.Build.ToString(CultureInfo.InvariantCulture);

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            if (key?.GetValue("DisplayVersion") is string display && display.Length > 0)
            {
                name = $"{name} {display}";
            }

            if (key?.GetValue("UBR") is int ubr)
            {
                build = $"{build}.{ubr.ToString(CultureInfo.InvariantCulture)}";
            }
        }
        catch (Exception ex)
        {
            // Registry read denied or the values absent: the build number on its own is still useful.
            Debug.WriteLine($"[Ripcord] Windows version detail unavailable: {ex.Message}");
        }

        return $"{name} · build {build}";
    }

    private enum PillTone
    {
        Neutral,
        Success,
        Caution,
    }

    /// <summary>
    /// Colour a status pill from the system fill palette. The brushes are resolved in code because the tone is
    /// only known once the probe finishes; a theme change while this page is open would leave the colours stale
    /// until the next navigation, which is an acceptable trade for one pill per fact in the markup rather than
    /// one per fact per outcome.
    /// </summary>
    private static void SetPill(Border pill, FontIcon icon, TextBlock label, PillTone tone, string text)
    {
        (string background, string foreground, string glyph) = tone switch
        {
            PillTone.Success => ("SystemFillColorSuccessBackgroundBrush", "SystemFillColorSuccessBrush", CheckGlyph),
            PillTone.Caution => ("SystemFillColorCautionBackgroundBrush", "SystemFillColorCautionBrush", WarningGlyph),
            _ => ("ControlFillColorDefaultBrush", "TextFillColorSecondaryBrush", AbsentGlyph),
        };

        if (LookupBrush(background) is { } backgroundBrush)
        {
            pill.Background = backgroundBrush;
        }

        if (LookupBrush(foreground) is { } foregroundBrush)
        {
            icon.Foreground = foregroundBrush;
            label.Foreground = foregroundBrush;
        }

        icon.Glyph = glyph;
        label.Text = text;
    }

    /// <summary>A theme brush by key, or null if the platform does not define it — never a throw over styling.</summary>
    private static Brush? LookupBrush(string key)
        => Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    /// <summary>
    /// Add or update a fact by label, keeping insertion order, so the clipboard text and the table agree even
    /// though the graphics rows are filled in asynchronously.
    /// </summary>
    private void Record(string label, string value)
    {
        int existing = _details.FindIndex(detail => detail.Label == label);
        if (existing >= 0)
        {
            _details[existing] = (label, value);
        }
        else
        {
            _details.Add((label, value));
        }
    }

    /// <summary>
    /// Put the device table on the clipboard as plain text, ready to paste into an issue.
    /// </summary>
    private async void OnCopyDetailsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var text = new StringBuilder();
            foreach ((string label, string value) in _details)
            {
                text.Append(label).Append(": ").AppendLine(value);
            }

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text.ToString());
            Clipboard.SetContent(package);

            CopyButtonText.Text = "Copied";
            await Task.Delay(TimeSpan.FromSeconds(2));
            CopyButtonText.Text = "Copy details";
        }
        catch (Exception ex)
        {
            // async void: this must not throw into the message loop.
            Debug.WriteLine($"[Ripcord] copying details failed: {ex.Message}");
        }
    }

    private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ConfigPathText.Text) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Ripcord] opening the settings folder failed: {ex.Message}");
        }
    }

    private static string ResolveVersion()
    {
        Assembly assembly = typeof(AboutPage).Assembly;

        // InformationalVersion carries the full build string when one is set; fall back to the assembly version.
        string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? assembly.GetName().Version?.ToString()
                         ?? "development build";

        // Trim any SourceLink commit suffix ("1.0.0+9b4938…"): it is noise in a short chip, and the version
        // itself is what identifies the build in a report.
        int plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }
}
