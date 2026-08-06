using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Presentation.Halyard.Pairing;

/// <summary>
/// Finds the registration cipher for a console family, from whichever source this build and machine can offer.
///
/// <para>
/// An interface rather than a static because the pairing flow needs to be testable without the constants being
/// present: a test wants to drive the "no usable cipher" path, and the real resolver's answer depends on
/// environment variables, the user's config directory, and whether the build bundled its interop constants.
/// </para>
/// </summary>
public interface IHalyardRegistrationCipherResolver
{
    /// <summary>
    /// The cipher for <paramref name="platform"/>. Never null: when nothing can serve the family the result is
    /// an unavailable cipher, so a caller checks <see cref="IHalyardRegistrationCipher.IsAvailable"/> rather
    /// than handling a null.
    /// </summary>
    /// <param name="source">
    /// Which source won, and — when a located fixture lost — why. Surfaced to the user verbatim on a pairing
    /// failure, so it is part of the contract rather than a debugging aid.
    /// </param>
    IHalyardRegistrationCipher Resolve(HalyardConsolePlatform platform, out string source);
}
