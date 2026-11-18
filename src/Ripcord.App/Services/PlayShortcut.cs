using System;
using System.IO;
using System.Reflection;

namespace Ripcord_App.Services;

/// <summary>
/// Writes a desktop shortcut that launches Ripcord straight into one console.
///
/// <para>
/// <b>Why a .lnk and not a jump-list entry.</b> A jump list needs package identity, so it exists in the MSIX
/// and silently does not in the zip. A shortcut file works in both, and it is what a Steam or
/// handheld-launcher user needs: "add a non-Steam game" wants a path and an argument.
/// </para>
///
/// <para>
/// <b>Why late binding and not IShellLink.</b> A hand-declared <c>IShellLinkW</c> means an eighteen-entry
/// vtable written by hand, where a slot out of order does not fail to compile but calls the wrong function
/// through a COM pointer, in a feature no unit test can exercise. <c>WScript.Shell</c> has shipped in Windows
/// since 1998 and does the same job.
/// </para>
/// </summary>
internal static class PlayShortcut
{
    /// <summary>
    /// Write "Play &lt;name&gt;.lnk" to the desktop, pointing at this executable with <c>--play</c>.
    ///
    /// <para>
    /// Returns the path written, or null when it could not be. Failure is a normal outcome rather than an
    /// exception worth propagating: the desktop may be redirected somewhere read-only or onto a share that is
    /// not there, and none of that should reach a player who clicked a menu item.
    /// </para>
    /// </summary>
    public static string? WriteToDesktop(string consoleName)
    {
        try
        {
            if (Environment.ProcessPath is not { Length: > 0 } exe)
            {
                return null;
            }

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop))
            {
                return null;
            }

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            object? shell = Activator.CreateInstance(shellType);
            if (shell is null)
            {
                return null;
            }

            string path = Path.Combine(desktop, Sanitise($"Play {consoleName}") + ".lnk");

            object? link = shellType.InvokeMember(
                "CreateShortcut", BindingFlags.InvokeMethod, null, shell, [path]);

            if (link is null)
            {
                return null;
            }

            Type linkType = link.GetType();
            Set(linkType, link, "TargetPath", exe);

            // Quoted, because console names have spaces far more often than not.
            Set(linkType, link, "Arguments", $"--play \"{consoleName}\"");
            Set(linkType, link, "WorkingDirectory", Path.GetDirectoryName(exe) ?? desktop);
            Set(linkType, link, "Description", $"Play {consoleName} with Ripcord");
            Set(linkType, link, "IconLocation", $"{exe}, 0");

            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);

            return File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            // See the summary: every failure here is environmental, and none of it is the player's problem.
            return null;
        }
    }

    private static void Set(Type type, object target, string property, string value)
        => type.InvokeMember(property, BindingFlags.SetProperty, null, target, [value]);

    /// <summary>
    /// Strip what a file name may not contain.
    ///
    /// <para>
    /// A console's name is whatever its owner typed, and people do put colons and slashes in them. The
    /// shortcut's file name is cosmetic — the argument inside it carries the real name — so replacing rather
    /// than rejecting is the right trade.
    /// </para>
    /// </summary>
    private static string Sanitise(string name)
    {
        foreach (char bad in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(bad, ' ');
        }

        name = name.Trim();
        return name.Length == 0 ? "Ripcord" : name;
    }
}
