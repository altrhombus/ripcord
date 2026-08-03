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
    }

    /// <summary>Whether to put the console into rest mode. Meaningful only when the dialog returned <c>Primary</c>.</summary>
    public bool RestConsole => RestCheckBox.IsChecked == true;
}
