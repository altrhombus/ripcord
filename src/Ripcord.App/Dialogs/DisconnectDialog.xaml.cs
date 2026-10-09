using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Ripcord_App.Dialogs;

/// <summary>
/// Confirms ending a live stream and offers the rest-mode choice for this disconnect. The checkbox starts on
/// the user's standing "Rest console when I disconnect" preference but is per-disconnect: changing it here does
/// not change the setting. On <c>Primary</c> (Disconnect) the choice is in <see cref="RestConsole"/>.
/// </summary>
public sealed partial class DisconnectDialog : ContentDialog
{
    public DisconnectDialog(bool defaultRest)
    {
        InitializeComponent();
        RestCheckBox.IsChecked = defaultRest;

        // Focus on Disconnect, not the checkbox. DefaultButton makes it the Enter target but does not move focus,
        // and WinUI focuses the first control in the content - the checkbox - so a pad user's reflexive A ticked
        // "put the console into rest mode" instead of disconnecting (visual audit, 2026-10-08).
        Opened += (_, _) => (GetTemplateChild("PrimaryButton") as Control)?.Focus(FocusState.Keyboard);
    }

    /// <summary>Whether to put the console into rest mode. Meaningful only when the dialog returned <c>Primary</c>.</summary>
    public bool RestConsole => RestCheckBox.IsChecked == true;
}
