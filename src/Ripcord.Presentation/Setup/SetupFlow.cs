using System.Globalization;
using Ripcord.Core.Consoles;
using Ripcord.Core.Input;
using Ripcord.Core.Sessions;
using Ripcord.Core.Settings;
using Ripcord.Presentation.Resources;
using Ripcord.Presentation.Settings;
using Ripcord.Presentation.Threading;

namespace Ripcord.Presentation.Setup;

/// <summary>
/// The first-run setup: the picture, a console, a controller.
///
/// <para>
/// <b>Why it exists (2026-10-05).</b> The defaults are H.264 without HDR, because every PC can decode H.264. HEVC
/// with HDR is the better picture where the PC can decode it: the console's SDR stream clips HDR games' highlights
/// when its own HDR is on. A setting nobody knows about is a setting nobody turns on, so the choice is put to
/// everyone once, at the start, with the better picture preselected where the PC can show it.
/// </para>
///
/// <para>
/// Every step can be passed over. Skipping the whole setup leaves the defaults and does not ask again; leaving
/// part-way asks again on the next start. Navigation stays with the front end: the console step is the add-console
/// page, which this flow is told about when it comes back.
/// </para>
/// </summary>
public sealed class SetupFlow : ObservableState<SetupFlowState>
{
    /// <summary>The setup a completed run records. Raise it to put a changed setup to everyone once more.</summary>
    public const int CurrentVersion = 1;

    private readonly ISettingsStore _settings;
    private readonly IPairedConsoleStore _consoles;
    private readonly IVideoCapabilitiesProbe _capabilities;
    private readonly HashSet<string> _consolesAtStart;

    private SetupStep _step;
    private bool _checking = true;
    private bool _hevcAvailable;
    private bool _displayHdr;
    private PictureChoice _choice = PictureChoice.MostCompatible;
    private PairedConsole? _added;
    private bool _padAttached;
    private PadFamily _padFamily;
    private bool _padSeen;
    private bool? _gameInputInstalled;
    private bool _finished;

    public SetupFlow(
        SetupScope scope,
        ISettingsStore settings,
        IPairedConsoleStore consoles,
        IVideoCapabilitiesProbe capabilities,
        IUiDispatcher dispatcher)
        : base(dispatcher)
    {
        Scope = scope;
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _consoles = consoles ?? throw new ArgumentNullException(nameof(consoles));
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _consolesAtStart = [.. LoadConsoles().Select(c => c.Id)];
        _step = scope == SetupScope.Full ? SetupStep.Welcome : SetupStep.Picture;
    }

    public SetupScope Scope { get; }

    /// <summary>The setup ended, finished or skipped. The front end leaves for the consoles.</summary>
    public event Action? Completed;

    /// <summary>
    /// Which setup this install needs, or null for none: none once a run has finished, the picture alone where a
    /// console is already paired (an install from before the setup existed), and the whole of it otherwise.
    /// </summary>
    public static SetupScope? ScopeFor(RipcordSettings settings, int pairedConsoles)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.SetupVersion >= CurrentVersion ? null
            : pairedConsoles > 0 ? SetupScope.PictureOnly
            : SetupScope.Full;
    }

    /// <summary>Check the PC for HEVC and an HDR display, which decide what the picture step offers.</summary>
    public async Task StartAsync()
    {
        bool hevc = await ProbeAsync(_capabilities.IsHevcDecodeAvailableAsync).ConfigureAwait(false);
        bool hdr = await ProbeAsync(_capabilities.IsHdrDisplayAvailableAsync).ConfigureAwait(false);

        Mutate(() =>
        {
            _checking = false;
            _hevcAvailable = hevc;
            _displayHdr = hdr;

            // The better picture preselected wherever the PC can decode it (the owner's decision, 2026-10-05).
            _choice = hevc ? PictureChoice.BestPicture : PictureChoice.MostCompatible;
        });
    }

    public void Choose(PictureChoice choice) => Mutate(() =>
    {
        if (!_checking && (choice == PictureChoice.MostCompatible || _hevcAvailable))
        {
            _choice = choice;
        }
    });

    /// <summary>The footer's main button. On the console step with nothing paired, the front end opens the add-console page instead.</summary>
    public void Next()
    {
        switch (_step)
        {
            case SetupStep.Welcome:
                Go(SetupStep.Picture);
                break;

            case SetupStep.Picture:
                if (_checking)
                {
                    return;
                }

                ApplyPicture();
                if (Scope == SetupScope.PictureOnly)
                {
                    Finish();
                }
                else
                {
                    Go(SetupStep.Console);
                }

                break;

            case SetupStep.Console:
                Go(SetupStep.Controller);
                break;

            case SetupStep.Controller:
                Go(SetupStep.Done);
                break;

            case SetupStep.Done:
                Finish();
                break;
        }
    }

    /// <summary>The footer's second button: skip the setup, keep the stored picture settings, or add a console later.</summary>
    public void Secondary()
    {
        switch (_step)
        {
            case SetupStep.Welcome:
            case SetupStep.Picture when Scope == SetupScope.PictureOnly:
                Finish();
                break;

            case SetupStep.Console when _added is null:
                Go(SetupStep.Controller);
                break;
        }
    }

    /// <summary>Step back; false when there is nowhere to go, which the front end reads as leaving.</summary>
    public bool Back()
    {
        SetupStep? previous = _step switch
        {
            SetupStep.Picture when Scope == SetupScope.Full => SetupStep.Welcome,
            SetupStep.Console => SetupStep.Picture,
            SetupStep.Controller => SetupStep.Console,
            SetupStep.Done => SetupStep.Controller,
            _ => null,
        };

        if (previous is not { } step)
        {
            return false;
        }

        Go(step);
        return true;
    }

    /// <summary>The add-console page closed. Whatever it paired that wasn't here when the setup began is the console.</summary>
    public void ReturnedFromAddConsole()
    {
        PairedConsole? added = LoadConsoles().LastOrDefault(c => !_consolesAtStart.Contains(c.Id));
        Mutate(() => _added = added ?? _added);
    }

    /// <summary>A controller connected or went away. Off the UI thread is fine.</summary>
    public void SetPadAttached(bool attached, PadFamily family) => Mutate(() =>
    {
        // The family is the pad last used, so it follows a change even after a press: the first frame of a pad
        // can arrive before the router has noticed which engine it came from.
        _padAttached = attached;
        _padFamily = family;
    });

    /// <summary>
    /// Whether GameInput, which Xbox controllers need, is installed. The front end asks (it is native); until it
    /// answers, nothing is said.
    /// </summary>
    public void SetGameInputInstalled(bool installed) => Mutate(() => _gameInputInstalled = installed);

    /// <summary>A button was pressed on a controller of <paramref name="family"/>. Off the UI thread is fine.</summary>
    public void PadPressed(PadFamily family) => Mutate(() =>
    {
        _padAttached = true;
        _padSeen = true;
        _padFamily = family;
    });

    protected override SetupFlowState Compose()
    {
        RipcordSettings stored = _settings.Current;
        bool full = Scope == SetupScope.Full;

        (string primary, bool primaryEnabled, string secondary) = _step switch
        {
            SetupStep.Welcome => (Strings.Setup_GetStarted, true, Strings.Setup_SkipSetup),
            SetupStep.Picture => (full ? Strings.Setup_Continue : Strings.Setup_Done, !_checking,
                full ? string.Empty : Strings.Setup_KeepMySettings),
            SetupStep.Console => _added is null
                ? (Strings.Setup_AddConsole, true, Strings.Setup_Later)
                : (Strings.Setup_Continue, true, string.Empty),
            SetupStep.Controller => (Strings.Setup_Continue, true, string.Empty),
            _ => (Strings.Setup_GoToConsoles, true, string.Empty),
        };

        return new SetupFlowState(
            Step: _step,
            Scope: Scope,
            Title: _step switch
            {
                SetupStep.Welcome => Strings.Setup_WelcomeTitle,
                SetupStep.Picture => Strings.Setup_PictureTitle,
                SetupStep.Console => Strings.Setup_ConsoleTitle,
                SetupStep.Controller => Strings.Setup_ControllerTitle,

                // "All set." over "Console: none yet" said the opposite of the line beneath it (visual audit,
                // 2026-10-08). With nothing paired, the headline says what is left rather than that nothing is.
                _ when _added is null && _consolesAtStart.Count == 0 => Strings.Setup_DoneTitleNoConsole,
                _ => Strings.Setup_DoneTitle,
            },
            ReachedDash: _step switch
            {
                SetupStep.Welcome => 0,
                SetupStep.Picture => 1,
                SetupStep.Console => 2,
                _ => 3,
            },
            ShowsDashes: full && _step != SetupStep.Welcome,

            PictureIntro: full ? Strings.Setup_PictureIntro : Strings.Setup_PictureOnlyIntro,
            Checking: _checking,
            BestPictureAvailable: _hevcAvailable,
            Choice: _choice,
            BestPictureLabel: Strings.Setup_BestPicture,
            BestPictureDetail: _displayHdr ? Strings.Setup_BestPictureHdrDisplay : Strings.Setup_BestPictureSdrDisplay,
            CompatibleLabel: Strings.Setup_MostCompatible,
            CompatibleDetail: Strings.Setup_MostCompatibleDetail,
            RecommendedLabel: Strings.Setup_Recommended,
            PictureNote: _checking ? Strings.Setup_Checking
                : _hevcAvailable ? string.Empty
                : Strings.Setup_NoHevc,
            PictureDefaults: string.Format(
                CultureInfo.CurrentCulture, Strings.Setup_PictureDefaults,
                stored.Height, stored.TargetFps, stored.BitrateKbps / 1000),

            ConsoleIntro: Strings.Setup_ConsoleIntro,
            ConsolePreflight: Strings.Setup_ConsolePreflight,
            ConsoleAdded: _added is not null,
            ConsoleStatus: _added is null
                ? string.Empty
                : string.Format(CultureInfo.CurrentCulture, Strings.Setup_ConsolePaired, _added.DisplayName),

            ControllerIntro: Strings.Setup_ControllerIntro,
            ControllerSeen: _padSeen,
            ControllerStatus: _padSeen
                ? PadName(_padFamily)
                : _padAttached ? Strings.Setup_PressAnyButton : Strings.Setup_NoController,
            ControllerDetail: _padSeen ? Strings.Setup_ButtonNamesFollow
                : _padAttached ? string.Empty
                : Strings.Setup_NoControllerDetail,
            GameInputNote: _gameInputInstalled == false ? Strings.Setup_GameInputMissing : string.Empty,
            ExitGestureLine: ExitButtons(stored.ExitGesture) is { } buttons
                ? string.Format(CultureInfo.CurrentCulture, Strings.Setup_ExitGesture, buttons)
                : Strings.Setup_ExitWithEsc,

            WelcomeBody: Strings.Setup_WelcomeBody,
            DonePicture: stored.Codec == VideoCodec.Hevc && stored.RequestHdr
                ? Strings.Setup_DonePictureBest
                : Strings.Setup_DonePictureCompatible,
            DoneConsole: _added is null
                ? Strings.Setup_DoneNoConsole
                : string.Format(CultureInfo.CurrentCulture, Strings.Setup_DoneConsole, _added.DisplayName),
            DoneController: _padSeen
                ? string.Format(CultureInfo.CurrentCulture, Strings.Setup_DoneController, PadName(_padFamily))
                : Strings.Setup_DoneNoController,

            PrimaryLabel: primary,
            PrimaryEnabled: primaryEnabled,
            SecondaryLabel: secondary,
            CanGoBack: _step switch
            {
                SetupStep.Welcome => false,
                SetupStep.Picture => full,
                _ => true,
            },
            BackLabel: Strings.Setup_Back);
    }

    /// <summary>
    /// The exit gesture's buttons as this step should name them: the pad in hand once there is one, both
    /// families before then. With nothing attached the family defaulted to the generic pad, so setup taught Xbox
    /// button names to someone who might be holding a DualSense, or nothing (visual audit, 2026-10-08).
    /// </summary>
    private string? ExitButtons(ExitGesture gesture)
    {
        if (_padAttached)
        {
            return ExitGestureDetector.Buttons(gesture, _padFamily);
        }

        return ExitGestureDetector.Buttons(gesture, PadFamily.Vendor) is { } vendor
            && ExitGestureDetector.Buttons(gesture, PadFamily.Generic) is { } generic
            ? string.Format(CultureInfo.CurrentCulture, Strings.Setup_ExitBothFamilies, vendor, generic)
            : null;
    }

    private static string PadName(PadFamily family)
        => family == PadFamily.Vendor ? Strings.Setup_PadPlayStation : Strings.Setup_PadOther;

    private void Go(SetupStep step) => Mutate(() => _step = step);

    /// <summary>Saved as soon as it's chosen, so a setup left part-way still keeps the picture.</summary>
    private void ApplyPicture()
    {
        bool best = _choice == PictureChoice.BestPicture && _hevcAvailable;
        _settings.Save(_settings.Current with
        {
            Codec = best ? VideoCodec.Hevc : VideoCodec.H264,
            RequestHdr = best,
        });
    }

    private void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _settings.Save(_settings.Current with { SetupVersion = CurrentVersion });
        Completed?.Invoke();
    }

    private List<PairedConsole> LoadConsoles()
    {
        try
        {
            return _consoles.Load();
        }
        catch (Exception)
        {
            // A store that can't be read has nothing the setup can use; the consoles page will say why.
            return [];
        }
    }

    private static async Task<bool> ProbeAsync(Func<Task<bool>> query)
    {
        try
        {
            return await query().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // As Settings does: when the PC can't be asked, offer only what certainly works.
            return false;
        }
    }
}
