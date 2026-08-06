namespace Ripcord.Presentation.Sessions;

/// <summary>
/// Read-only access to whatever is decoding and presenting video.
///
/// <para>
/// The seam exists because the decode pipeline is the one thing in the session surface that is irreducibly a
/// device: D3D12, a native swap chain, an adapter LUID. None of that can cross into the portable layer, but
/// almost everything the diagnostics overlay <em>says</em> about it is arithmetic and string formatting over
/// numbers — including the sampling-interval rule that stops a delayed timer tick reporting 1610 fps, which is
/// exactly the sort of thing that should be tested rather than observed in the wild.
/// </para>
///
/// <para>
/// Deliberately read-only. Creating the device, choosing an adapter, binding a swap chain and resizing it stay
/// with the front end; this is only the question "what is it doing right now?".
/// </para>
/// </summary>
public interface IVideoPipelineStats
{
    /// <summary>
    /// False before the pipeline exists, or after it has been torn down. The overlay runs from before the
    /// session starts until after it ends, so "there is nothing to read yet" is a normal state, not an error.
    /// </summary>
    bool IsReady { get; }

    /// <summary>The graphics adapter in use, in the form a user could repeat back in a bug report.</summary>
    string AdapterDescription { get; }

    /// <summary>Sample the current counters. Returns the default snapshot when <see cref="IsReady"/> is false.</summary>
    VideoPipelineSnapshot Read();
}
