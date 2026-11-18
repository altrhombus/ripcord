using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ripcord.Core.Consoles;
using Windows.UI.StartScreen;

namespace Ripcord_App.Services;

/// <summary>
/// Puts the paired consoles on the app's taskbar jump list, so a right-click on a pinned icon goes straight
/// into a game.
///
/// <para>
/// <b>Guarded.</b> <c>JumpList.IsSupported()</c> is false without package identity, so the MSIX gets a jump
/// list and the zip silently does not. That is the intended outcome: the shortcut writer covers the zip, and
/// Steam and handheld launchers besides.
/// </para>
///
/// <para>
/// Every failure is swallowed. This is a convenience that runs during startup and whenever the console list
/// changes, and a jump list that could not be rebuilt is not worth interrupting somebody to say.
/// </para>
/// </summary>
internal static class PlayJumpList
{
    /// <summary>
    /// Rebuild the list from the consoles that are paired now.
    ///
    /// <para>
    /// Rebuilt whole rather than diffed: the list is at most a handful of entries, and reconciling it
    /// against what Windows currently holds would be more code than writing it again.
    /// </para>
    /// </summary>
    public static async Task RefreshAsync(IReadOnlyList<PairedConsole> consoles)
    {
        try
        {
            if (consoles is null || !JumpList.IsSupported())
            {
                return;
            }

            JumpList list = await JumpList.LoadCurrentAsync();
            list.Items.Clear();
            list.SystemGroupKind = JumpListSystemGroupKind.None;

            // Most recently reached for first, which is the order somebody scanning a context menu wants.
            // The stamp is written on the attempt rather than on a successful stream, so it tracks intent.
            foreach (PairedConsole console in consoles
                         .OrderByDescending(c => c.LastConnectedUtc ?? DateTimeOffset.MinValue)
                         .Take(5))
            {
                JumpListItem item = JumpListItem.CreateWithArguments(
                    $"--play \"{console.DisplayName}\"",
                    console.DisplayName);

                item.Description = $"Play {console.DisplayName}";
                list.Items.Add(item);
            }

            await list.SaveAsync();
        }
        catch (Exception)
        {
            // See the summary. A jump list that failed to rebuild is not worth a word to anyone.
        }
    }
}
