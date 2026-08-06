using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Ripcord.Presentation;
using Ripcord.Presentation.Consoles;
using Ripcord.Presentation.Settings;
using Ripcord_App.Input;
using Ripcord_App.Dialogs;
using Ripcord_App.Services;

namespace Ripcord_App.Pages;

/// <summary>
/// The tunables. Everything here was previously a compile-time constant scattered across the app, which is why
/// this page used to read "This is the Settings page".
///
/// <para>
/// Changes save immediately — there is no OK/Cancel, matching how Windows 11 settings behave. Video and GPU
/// changes apply to the <em>next</em> connection, because both require rebuilding the decode pipeline (and, for
/// the GPU, a new device and swap chain).
/// </para>
///
/// <para>
/// Presentation only. Which options exist, what each help line says, when HDR is offerable, and whether a change
/// is worth saving all live in <see cref="SettingsViewModel"/>.
/// </para>
///
/// <para>
/// <b>Handlers are attached in code, after the first render — not in markup.</b> XAML parsing raises change
/// events on its own: <c>Slider.Minimum="2"</c> moves <c>Value</c> from 0 to 2 and fires <c>ValueChanged</c>
/// during <c>InitializeComponent</c>. Markup-attached handlers therefore ran before the page had loaded
/// anything, which is what the old <c>_loading</c> bool existed to paper over — a flag checked in fourteen
/// places and one more to forget in the fifteenth. Attaching afterwards means the event cannot happen at all.
/// </para>
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly SettingsViewModel _viewModel;

    // Guards the projection, and is NOT _loading under another name. That flag protected against events fired
    // before the page had any data, and had to be checked in fourteen handlers plus inside the save; this is set
    // and cleared in exactly one method and read in exactly one place. It earns its keep on one specific case:
    // clearing a ComboBox's items drives SelectedIndex to -1 and raises SelectionChanged on the way past, and a
    // handler seeing -1 would clamp it to a real option and save a setting the user never touched.
    private bool _rendering;

    public SettingsPage()
    {
        _viewModel = App.Services.CreateSettingsViewModel();

        InitializeComponent();

        _viewModel.PropertyChanged += (_, _) => Render(_viewModel.State);
    }

    /// <summary>
    /// async void, confined to this one launcher and unable to throw: everything inside is either guarded by the
    /// view-model or caught here. The load has to be asynchronous because the capability probes are native and
    /// crash the process if run on this thread — see <see cref="IVideoCapabilitiesProbe"/>.
    /// </summary>
    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.LoadAsync();
        }
        catch (Exception ex)
        {
            // The page must stay reachable even when something it reads is broken — it is where a user goes to
            // fix a bad configuration. Rendering still happens below, with whatever the view-model settled on.
            Debug.WriteLine($"[Ripcord] settings load failed: {ex}");
        }

        Render(_viewModel.State);
        WireHandlers();
    }

    /// <summary>
    /// Attach the change handlers. Once, after the first render — see the note on the class.
    /// </summary>
    private void WireHandlers()
    {
        ResolutionCombo.SelectionChanged += (_, _) => Edit(() => _viewModel.SetResolution(ResolutionCombo.SelectedIndex));
        UpscaleCombo.SelectionChanged += (_, _) => Edit(() => _viewModel.SetUpscale(UpscaleCombo.SelectedIndex));
        CodecCombo.SelectionChanged += (_, _) => Edit(() => _viewModel.SetCodec(CodecCombo.SelectedIndex));
        // The one handler that awaits: picking "a specific GPU" has to go and enumerate them off-thread.
        GpuCombo.SelectionChanged += async (_, _) =>
        {
            if (_rendering)
            {
                return;
            }

            try
            {
                await _viewModel.SetGpuPreferenceAsync(GpuCombo.SelectedIndex);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Ripcord] GPU preference change failed: {ex}");
            }
        };
        AdapterCombo.SelectionChanged += (_, _) => Edit(() => _viewModel.SetAdapter(AdapterCombo.SelectedIndex));
        ExitGestureCombo.SelectionChanged += (_, _) => Edit(() => _viewModel.SetExitGesture(ExitGestureCombo.SelectedIndex));

        BitrateSlider.ValueChanged += (_, _) => Edit(() => _viewModel.SetBitrateMbps(BitrateSlider.Value));
        DeadzoneSlider.ValueChanged += (_, _) => Edit(() => _viewModel.SetDeadzone(DeadzoneSlider.Value));

        HdrToggle.Toggled += (_, _) => Edit(() => _viewModel.SetRequestHdr(HdrToggle.IsOn));
        AdaptiveToggle.Toggled += (_, _) => Edit(() => _viewModel.SetAdaptiveQuality(AdaptiveToggle.IsOn));
        ConnectionQualityToggle.Toggled += (_, _) => Edit(() => _viewModel.SetReportConnectionQuality(ConnectionQualityToggle.IsOn));
        KeyboardToggle.Toggled += (_, _) => Edit(() => _viewModel.SetKeyboardEnabled(KeyboardToggle.IsOn));
        FullScreenToggle.Toggled += (_, _) => Edit(() => _viewModel.SetFullScreenOnConnect(FullScreenToggle.IsOn));
        ConfirmOnDisconnectToggle.Toggled += (_, _) => Edit(() => _viewModel.SetConfirmOnDisconnect(ConfirmOnDisconnectToggle.IsOn));
        RestOnDisconnectToggle.Toggled += (_, _) => Edit(() => _viewModel.SetRestOnDisconnect(RestOnDisconnectToggle.IsOn));
        DiagnosticsToggle.Toggled += (_, _) => Edit(() => _viewModel.SetShowDiagnostics(DiagnosticsToggle.IsOn));
        LargeUiToggle.Toggled += (_, _) => Edit(() => _viewModel.SetLargeUiScale(LargeUiToggle.IsOn));
    }

    /// <summary>Forward a user edit, unless the change came from Render assigning the control itself.</summary>
    private void Edit(Action edit)
    {
        if (!_rendering)
        {
            edit();
        }
    }

    // ---- render ----

    /// <summary>
    /// Project the whole state onto the controls. One method rather than per-property handlers: the state
    /// arrives as one value, so the codec picker and the HDR toggle it gates cannot disagree — which is exactly
    /// what they used to do when a change handler updated one and left the other until the page was reopened.
    /// </summary>
    private void Render(SettingsViewState s)
    {
        _rendering = true;
        try
        {
            FillCombo(ResolutionCombo, s.ResolutionOptions, s.ResolutionIndex);
            FillCombo(UpscaleCombo, SettingsViewModel.UpscaleLabels, s.UpscaleIndex);
            FillCombo(CodecCombo, s.CodecOptions, s.CodecIndex);
            FillCombo(GpuCombo, SettingsViewModel.GpuLabels, s.GpuPreferenceIndex);
            FillCombo(ExitGestureCombo, SettingsViewModel.ExitGestureLabels, s.ExitGestureIndex);
            FillCombo(AdapterCombo, s.AdapterOptions, s.AdapterIndex);

            BitrateSlider.Value = Math.Clamp(s.BitrateMbps, BitrateSlider.Minimum, BitrateSlider.Maximum);
            BitrateValueText.Text = s.BitrateLabel;

            DeadzoneSlider.Value = Math.Clamp(s.UiStickDeadzone, DeadzoneSlider.Minimum, DeadzoneSlider.Maximum);
            DeadzoneValueText.Text = s.DeadzoneLabel;

            CodecCombo.IsEnabled = s.CodecPickerEnabled;
            CodecCard.Description = s.CodecHelp;
            HdrToggle.IsOn = s.RequestHdr;
            HdrToggle.IsEnabled = s.HdrToggleEnabled;
            HdrHelpText.Text = s.HdrHelp;
            RenderHdrChecklist(s.HdrChecks);

            AdapterCard.Visibility = Vis(s.AdapterPickerVisible);
            AdapterWarning.Message = s.AdapterWarning;
            AdapterWarning.IsOpen = s.AdapterWarningVisible;

            AdaptiveToggle.IsOn = s.AdaptiveQuality;
            ConnectionQualityToggle.IsOn = s.ReportConnectionQuality;
            KeyboardToggle.IsOn = s.KeyboardEnabled;
            KeyBindingsCard.Description = s.KeyboardSummary;
            ExitGestureCard.Description = s.ExitGestureDescription;

            FullScreenToggle.IsOn = s.FullScreenOnConnect;
            ConfirmOnDisconnectToggle.IsOn = s.ConfirmOnDisconnect;
            RestOnDisconnectToggle.IsOn = s.RestConsoleOnDisconnect;
            DiagnosticsToggle.IsOn = s.ShowDiagnosticsOverlay;
            LargeUiToggle.IsOn = s.LargeUiScale;

            RenderCredentialBar(s);
        }
        finally
        {
            _rendering = false;
        }
    }

    /// <summary>
    /// Fill a picker, but only when its contents have actually changed — a rebuild drops the selection and
    /// restores it, which is churn on every render for a list that changes at most once per visit.
    /// </summary>
    private static void FillCombo(ComboBox combo, IReadOnlyList<string> options, int selected)
    {
        if (combo.Items.Count != options.Count || !SameItems(combo, options))
        {
            combo.Items.Clear();
            foreach (string option in options)
            {
                combo.Items.Add(option);
            }
        }

        combo.SelectedIndex = selected >= 0 && selected < options.Count ? selected : -1;
    }

    private static bool SameItems(ComboBox combo, IReadOnlyList<string> options)
    {
        for (int i = 0; i < options.Count; i++)
        {
            if (combo.Items[i] as string != options[i])
            {
                return false;
            }
        }

        return true;
    }

    private void RenderCredentialBar(SettingsViewState s)
    {
        // A load failure outranks the credential status: this page is where a user goes to fix a bad
        // configuration, so what could not be read matters more than what is protected.
        if (s.LoadError is { } error)
        {
            CredentialProtectionBar.Severity = InfoBarSeverity.Error;
            CredentialProtectionBar.Title = "Some settings couldn't be loaded";
            CredentialProtectionBar.Message = error;
            CredentialProtectionBar.IsOpen = true;
            return;
        }

        CredentialProtectionBar.Severity = s.CredentialTone switch
        {
            StatusTone.Positive => InfoBarSeverity.Success,
            StatusTone.Caution => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational,
        };

        CredentialProtectionBar.Title = s.CredentialTitle;
        CredentialProtectionBar.Message = s.CredentialMessage;
    }

    private void RenderHdrChecklist(IReadOnlyList<HdrCheck> checks)
    {
        HdrChecklist.Children.Clear();

        foreach (HdrCheck check in checks)
        {
            HdrChecklist.Children.Add(BuildHdrRow(check));
        }
    }

    private static StackPanel BuildHdrRow(HdrCheck check)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };

        // Glyph carries the state, colour reinforces it. Not colour alone: this has to stay legible to a
        // colour-blind user and in high contrast, where the theme brushes collapse toward the same value.
        row.Children.Add(new FontIcon
        {
            // Caption-sized, because the glyph sits inline with caption text. Through the token, not a
            // literal, for the same reason the XAML goes through it: a literal is a size no scale factor
            // can reach.
            FontSize = (double)Application.Current.Resources["RipcordIconSizeCaption"],
            VerticalAlignment = VerticalAlignment.Center,
            Glyph = check.State switch
            {
                HdrCheckState.Met => "",     // CheckMark
                HdrCheckState.Unmet => "",   // Cancel
                _ => "",                     // Info
            },
            Foreground = (Brush)Application.Current.Resources[check.State switch
            {
                HdrCheckState.Met => "SystemFillColorSuccessBrush",
                HdrCheckState.Unmet => "SystemFillColorCautionBrush",
                _ => "TextFillColorTertiaryBrush",
            }],
        });

        var text = new TextBlock
        {
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };

        text.Inlines.Add(new Run { Text = check.Label });

        if (check.Hint is { Length: > 0 } hint)
        {
            // The remedy sits on the same line, dimmer: it is only wanted when the check is unmet, and a
            // separate line per hint would double the height of a list that is meant to be glanceable.
            text.Inlines.Add(new Run
            {
                Text = "  " + hint,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            });
        }

        row.Children.Add(text);
        return row;
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    // ---- actions ----

    /// <summary>
    /// Open the rebinding dialog. Its result goes to the view-model, which merges it onto the keyboard toggle
    /// rather than replacing the whole bindings record.
    /// </summary>
    private async void KeyBindings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // A page, not a dialog: rebinding by capture wants every key, and a dialog reserves Enter and
            // Escape. It also saves as it goes, so there is nothing to read back here.
            Frame.Navigate(typeof(KeyBindingsPage));
        }
        catch (Exception ex)
        {
            // async void: this cannot be allowed to throw into the message loop.
            Debug.WriteLine($"[Ripcord] key bindings navigation failed: {ex.Message}");
        }
    }

}
