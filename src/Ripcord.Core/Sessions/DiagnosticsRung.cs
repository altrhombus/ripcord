namespace Ripcord.Core.Sessions;

/// <summary>
/// How much of the stream HUD is on screen.
///
/// <para>
/// The project's disclosure ladder, finally implemented as a ladder. The diagnostics panel was the stated
/// thesis for it — three rungs, each entered deliberately — while being, in fact, one drawer holding
/// everything: eight groups at uniform caption weight in a single scrolling column, all of it rung 3, with no
/// rung 1 and no rung 2 at all.
/// </para>
///
/// <para>
/// Each rung answers one question, and a rung only carries facts that question needs:
/// </para>
/// <list type="bullet">
///   <item><b>Hidden</b> — <i>is anything wrong?</i> Answered without the HUD at all, by the rung-1 notice,
///   which appears on its own when the answer is yes and says nothing when it is no.</item>
///   <item><b>Summary</b> — <i>is it me or the network?</i> The verdict, the four numbers that bear on it,
///   and what the picture is actually getting.</item>
///   <item><b>Full</b> — <i>what exactly is happening?</i> Every instrument, for someone who has decided to
///   look properly.</item>
/// </list>
///
/// <para>
/// Rung 1 is deliberately not a member here. It is not a state anyone selects: it appears and clears on its
/// own (see the health alert gate), and making it a rung would imply it could be chosen, or worse,
/// dismissed — which would mean dismissing the only unprompted thing the app says while a game is running.
/// </para>
///
/// <para>
/// It lives in Core rather than beside the view-model because it is also a persisted preference: the
/// settings record has to name the rung a stream opens at, and Core cannot reference the layer above it.
/// </para>
/// </summary>
public enum DiagnosticsRung
{
    /// <summary>Nothing but the game, and the rung-1 notice if there is something to say.</summary>
    Hidden,

    /// <summary>The summary strip: verdict, four numbers, capability pills.</summary>
    Summary,

    /// <summary>The full instrument panel.</summary>
    Full,
}
