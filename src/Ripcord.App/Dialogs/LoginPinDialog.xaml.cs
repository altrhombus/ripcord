using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Ripcord.Core.Input;
using Ripcord_App.Converters;
using Ripcord.Presentation.Halyard.Sessions;

namespace Ripcord_App.Dialogs;

/// <summary>
/// Prompts for the console's user login passcode when a session finds the console signed out. Distinct from
/// the pairing code: this is the per-user login passcode set on the console. On <c>Primary</c>, or when
/// <see cref="SubmittedByPad"/> is set, the entered digits are in <see cref="Pin"/>.
///
/// <para>
/// <b>The pad types it too,</b> with the console's own passcode buttons (<see cref="HalyardPasscodePad"/>), so a
/// player on the couch enters it the way they would on the console. Shown with <c>readsPad</c>, which stops
/// those buttons also moving focus.
/// </para>
/// </summary>
public sealed partial class LoginPinDialog : ContentDialog
{
    /// <summary>Below this the Sign in button stays disabled — a passcode is at least four digits.</summary>
    private const int MinPasscodeLength = 4;

    /// <summary>What the hint bar says the two free buttons do while this is up.</summary>
    public static readonly IReadOnlyList<InputPrompt> PadPrompts =
    [
        new(PromptButton.South, "Sign in"),
        new(PromptButton.East, "Delete"),
    ];

    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    // Touched only on the polling thread that raises FrameReceived; see HalyardPasscodePad. Its baseline is what
    // the pad held as the prompt was built, so the press that started the connect does not type or submit.
    private readonly HalyardPasscodePad _pad = new(App.Input.LastFrame);

    public LoginPinDialog(bool isRetry = false)
    {
        InitializeComponent();
        if (isRetry)
        {
            RetryNotice.Visibility = Visibility.Visible;
        }

        RenderPad();

        Opened += (_, _) =>
        {
            App.Input.FrameReceived += OnFrame;
            App.Input.PadFamilyChanged += OnPadChanged;
            App.Input.PadAttachedChanged += OnPadChanged;
        };
        Closed += (_, _) =>
        {
            App.Input.FrameReceived -= OnFrame;
            App.Input.PadFamilyChanged -= OnPadChanged;
            App.Input.PadAttachedChanged -= OnPadChanged;
        };
    }

    // Raised off the UI thread, as the hint bar's are. The legend follows the pad in hand, as the hint bar does:
    // read once at open, it went on naming a DualSense's buttons after an Xbox pad took over (2026-10-02).
    private void OnPadChanged<T>(T _) => _dispatcher.TryEnqueue(RenderPad);

    /// <summary>The pad section for the pad in hand, or none without one.</summary>
    private void RenderPad()
    {
        PadLegend.Children.Clear();

        bool attached = App.Input.PadAttached;
        if (attached)
        {
            BuildPadLegend(App.Input.PadFamily);
        }

        PadSection.Visibility = attached ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The entered passcode, valid only when the dialog returned <c>Primary</c> or the pad submitted it.</summary>
    public string Pin => PinBox.Password;

    /// <summary>
    /// True when cross signed in. A dialog closed from code reports <c>None</c> whatever closed it, so the caller
    /// reads this beside the result.
    /// </summary>
    public bool SubmittedByPad { get; private set; }

    private void OnPinChanged(object sender, RoutedEventArgs e)
    {
        // Keep the box to digits, so a paste or a stray key cannot produce a passcode the console will reject
        // and cannot be submitted as something non-numeric. A PasswordBox has no caret to restore.
        string digits = new(System.Linq.Enumerable.Where(PinBox.Password, char.IsDigit).ToArray());
        if (digits != PinBox.Password)
        {
            PinBox.Password = digits;
        }

        IsPrimaryButtonEnabled = digits.Length >= MinPasscodeLength;
    }

    /// <summary>The console's table: each digit over the control that types it, 1 to 5 above 6 to 0.</summary>
    private void BuildPadLegend(PadFamily family)
    {
        const int PerRow = 5;

        for (int i = 0; i < HalyardPasscodePad.Keys.Count; i++)
        {
            PasscodeKey key = HalyardPasscodePad.Keys[i];
            string name = key.NameFor(family);

            var cell = new Border
            {
                Background = ThemeBrush.Lookup("CardBackgroundFillColorSecondaryBrush"),
                BorderBrush = ThemeBrush.Lookup("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4, 6, 4, 6),
                Child = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = key.Digit.ToString(),
                            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                        new TextBlock
                        {
                            Text = name,
                            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                            Foreground = ThemeBrush.Lookup("TextFillColorSecondaryBrush"),
                            HorizontalAlignment = HorizontalAlignment.Center,
                        },
                    },
                },
            };

            AutomationProperties.SetName(cell, $"{key.Digit}: {name}");
            Grid.SetRow(cell, i / PerRow);
            Grid.SetColumn(cell, i % PerRow);
            PadLegend.Children.Add(cell);
        }

        PadActions.Text = string.Join("   ", PadPrompts.Select(p => ButtonLabels.Describe(p, family)));
    }

    private void OnFrame(ControllerStateFrame frame)
    {
        // Read on the polling thread, every frame, so the reader's edges stay true even while something else is on
        // top; acted on only while this prompt owns the pad (a soft keyboard over it, say, does not).
        IReadOnlyList<PasscodePadInput> inputs = _pad.Read(frame);
        if (inputs.Count == 0 || App.Input.Scopes.Top?.ReadsPad != true)
        {
            return;
        }

        _dispatcher.TryEnqueue(() =>
        {
            foreach (PasscodePadInput input in inputs)
            {
                Apply(input);
            }
        });
    }

    private void Apply(PasscodePadInput input)
    {
        App.Input.ReportControllerActivity();
        string text = PinBox.Password;

        switch (input.Action)
        {
            case PasscodePadAction.Digit when text.Length < PinBox.MaxLength:
                PinBox.Password = text + input.Digit;
                break;

            case PasscodePadAction.Back when text.Length > 0:
                PinBox.Password = text[..^1];
                break;

            case PasscodePadAction.Back:
                // Nothing left to delete: circle leaves, as it does everywhere else.
                Hide();
                break;

            case PasscodePadAction.Submit when IsPrimaryButtonEnabled && !SubmittedByPad:
                SubmittedByPad = true;
                Hide();
                break;
        }
    }
}
