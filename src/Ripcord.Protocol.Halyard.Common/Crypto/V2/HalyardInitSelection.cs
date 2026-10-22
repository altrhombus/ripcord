namespace Ripcord.Protocol.Halyard.Common.Crypto.V2;

/// <summary>Which <c>/sess/init</c> flavour a session opens with (spec §3).</summary>
public enum HalyardInitFlavor
{
    /// <summary>
    /// v1 init (the vendor's "type 3"): RP-Registkey + version headers, v1 control-field crypto. PS4 always;
    /// PS5 when no v2 token is held and the console never advertised GCM; and the fallback after a v2 attempt
    /// fails on this connection.
    /// </summary>
    V1,

    /// <summary>
    /// v2 init (the vendor's "type 1"): RP-Pubkey + RP-Hmac and the GCM control envelope. Chosen when a rolled
    /// token is held, or the console advertised <c>RP-SupportGcm</c> at registration.
    /// </summary>
    V2,
}

/// <summary>
/// The v1/v2 <c>/sess/init</c> selection (spec §3). Pure decision logic the session wiring consumes: it owns no
/// state and does no I/O, so it can be unit-tested without a console.
/// </summary>
public static class HalyardInitSelection
{
    /// <summary>
    /// Pick the init flavour: v2 when a rolled token is held <b>or</b> the console advertised
    /// <c>RP-SupportGcm</c>, v1 otherwise — and always v1 once a v2 attempt on this connection has failed (the
    /// console answers <c>0x80108d6c</c>, clears its GCM flag, and the client retries as v1).
    ///
    /// <para>PS4 is v1-only (v2 is PS5-only, spec §1), which falls out naturally: a PS4 pairing never holds a v2
    /// token and a PS4 never advertises GCM, so the caller passes <c>false</c>/<c>false</c> and gets v1.</para>
    /// </summary>
    /// <param name="tokenHeld">A usable rolled type-11 token is persisted for this console
    /// (<see cref="HalyardSessionToken.HasRolledToken"/>).</param>
    /// <param name="consoleSupportsGcm">The console advertised <c>RP-SupportGcm</c> at registration.</param>
    /// <param name="v2PreviouslyFailed">A v2 attempt on this connection already failed with <c>0x80108d6c</c>.</param>
    public static HalyardInitFlavor Select(bool tokenHeld, bool consoleSupportsGcm, bool v2PreviouslyFailed)
    {
        if (v2PreviouslyFailed)
            return HalyardInitFlavor.V1;

        return tokenHeld || consoleSupportsGcm ? HalyardInitFlavor.V2 : HalyardInitFlavor.V1;
    }
}
