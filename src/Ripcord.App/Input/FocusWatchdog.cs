using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;

namespace Ripcord_App.Input;

/// <summary>
/// Puts focus back when it goes missing, without waiting to be asked.
///
/// <para>
/// <b>Why this is a standing watch rather than a fix at each site.</b> Focus disappears for reasons a page
/// cannot enumerate: the focused element is collapsed by a step change, its container is recycled out of a
/// list, a popup closes and restores nothing, the user clicks the empty space beside the content. Each of
/// those was found and patched individually at some point, and the next one was always found by a person
/// holding a pad rather than by the person who wrote the code. So the invariant is enforced in one place
/// instead — <em>something is always focused</em> — and new surfaces inherit it without knowing it exists.
/// </para>
///
/// <para>
/// <b>Why the re-check is deferred rather than done in the handler.</b> <c>LostFocus</c> fires on every
/// ordinary move, and it fires <em>before</em> the new element reports focus — so at handler time nothing has
/// focus even in the completely healthy case. Re-seeding there would fight every navigation the user makes.
/// Enqueuing the question at low priority asks it after the move has settled, at which point "still nothing
/// focused" means what it says.
/// </para>
///
/// <para>
/// <b>What it deliberately does not do:</b> move focus that already exists. It only ever acts on <em>nowhere
/// useful</em>, so it can never take the caret off something the user is aiming at.
/// </para>
/// </summary>
public sealed class FocusWatchdog(DispatcherQueue dispatcher, Func<bool> needsSeed, Action seed) : IDisposable
{
    private bool _checkQueued;
    private bool _running;

    public void Start()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        FocusManager.LostFocus += OnLostFocus;
    }

    public void Dispose()
    {
        if (!_running)
        {
            return;
        }

        _running = false;
        FocusManager.LostFocus -= OnLostFocus;
    }

    private void OnLostFocus(object? sender, FocusManagerLostFocusEventArgs e)
    {
        // One outstanding question at a time. A single navigation can raise this more than once, and each would
        // otherwise queue its own identical check.
        if (_checkQueued)
        {
            return;
        }

        _checkQueued = true;

        dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _checkQueued = false;

            try
            {
                if (needsSeed())
                {
                    seed();
                }
            }
            catch (Exception ex)
            {
                // A focus check must never be able to take the process down; a window mid-teardown is the
                // ordinary way this throws.
                System.Diagnostics.Debug.WriteLine($"Focus watchdog error: {ex}");
            }
        });
    }
}
