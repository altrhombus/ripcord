using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;

namespace Ripcord_App.Services;

/// <summary>
/// Says a change out loud to a screen reader. Connect progress, a failure, a pairing result and a health alert
/// all changed text on screen and were never announced, so Narrator users heard nothing until they went looking
/// (review, 2026-10-05).
///
/// <para>
/// A UI Automation notification rather than a live region: a live region needs the app to raise its change event
/// anyway, and a notification carries the words, so what is heard is exactly what changed.
/// </para>
/// </summary>
internal static class Announcer
{
    /// <summary>
    /// Announce <paramref name="text"/> from <paramref name="anchor"/>. <paramref name="important"/> is for a failure
    /// or an alert, which may interrupt; progress waits its turn and replaces the one before it. Never throws.
    /// </summary>
    public static void Announce(UIElement anchor, string? text, bool important = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            AutomationPeer? peer = FrameworkElementAutomationPeer.FromElement(anchor)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(anchor);
            peer?.RaiseNotificationEvent(
                AutomationNotificationKind.Other,
                important ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.MostRecent,
                text,
                important ? "RipcordAlert" : "RipcordStatus");
        }
        catch (Exception)
        {
            // An element with no peer, or no screen reader listening: nothing to say, and nothing worth failing.
        }
    }
}
