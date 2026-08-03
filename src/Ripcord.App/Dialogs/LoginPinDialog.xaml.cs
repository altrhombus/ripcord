using Microsoft.UI.Xaml.Controls;

namespace Ripcord_App.Dialogs;

/// <summary>
/// Prompts for the console's user login passcode when a session finds the console signed out. Distinct from
/// the pairing code: this is the per-user login passcode set on the console. On <c>Primary</c> the entered
/// digits are in <see cref="Pin"/>.
/// </summary>
public sealed partial class LoginPinDialog : ContentDialog
{
    /// <summary>Below this the Sign in button stays disabled — a passcode is at least four digits.</summary>
    private const int MinPasscodeLength = 4;

    public LoginPinDialog()
    {
        InitializeComponent();
    }

    /// <summary>The entered passcode, valid only when the dialog returned <c>Primary</c>.</summary>
    public string Pin => PinBox.Text;

    private void OnPinChanged(object sender, TextChangedEventArgs e)
    {
        // Keep the box to digits, so a paste or a stray key cannot produce a passcode the console will reject
        // and cannot be submitted as something non-numeric.
        string digits = new(System.Linq.Enumerable.Where(PinBox.Text, char.IsDigit).ToArray());
        if (digits != PinBox.Text)
        {
            int caret = PinBox.SelectionStart;
            PinBox.Text = digits;
            PinBox.SelectionStart = System.Math.Min(caret, digits.Length);
        }

        IsPrimaryButtonEnabled = digits.Length >= MinPasscodeLength;
    }
}
