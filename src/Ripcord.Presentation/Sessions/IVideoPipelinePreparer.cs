namespace Ripcord.Presentation.Sessions;

using Ripcord.Core.Sessions;

/// <summary>
/// Brings up the decode/present pipeline for a session that is about to start.
///
/// <para>
/// A device gets a seam. Behind this one sits D3D12 device creation, adapter selection and decoder
/// instantiation — none of it expressible without a GPU, and all of it the reason <see cref="ConnectFlow"/>
/// could not be portable while the sequence lived in the page.
/// </para>
///
/// <para>
/// <b>Failure is an exception, not a result, and that is deliberate.</b> Every other seam in this layer
/// reports refusal as a value, because a refusal there is an ordinary answer the UI has to render. Here it is
/// not: the front end already surfaces whatever went wrong through the exception's own message, and the
/// recorded history of this call is that it throws in ways nobody enumerated — an unfamiliar adapter, a
/// missing decoder, a driver that fails at device creation. A result type would have to carry an opaque
/// string anyway, so it would be a worse exception wearing a better name.
/// </para>
/// </summary>
public interface IVideoPipelinePreparer
{
    /// <summary>
    /// Create the graphics device and decoder for <paramref name="config"/>. Throws if the pipeline cannot be
    /// brought up; the exception message is shown to the user, so implementations owe it a readable one.
    /// </summary>
    Task PrepareAsync(SessionConfig config, CancellationToken cancellationToken);
}
