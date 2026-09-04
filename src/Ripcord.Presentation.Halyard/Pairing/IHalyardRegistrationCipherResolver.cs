using Ripcord.Protocol.Halyard.Common.Crypto;

namespace Ripcord.Presentation.Halyard.Pairing;

/// <summary>
/// What one resolution produced: the cipher, where it came from, and the field-cipher context key that came
/// with it.
///
/// <para>
/// The context key is here rather than being looked up separately because the account ("web") pairing route
/// needs it — the console's delivered registration seed arrives field-encrypted, and decrypting it uses the
/// same context key the registration cipher was built with. Resolving it through a second path would let a
/// developer's local fixture serve the registration while the bundle served the seed decrypt, which fails as a
/// wrong key deep inside a pairing rather than as a missing file.
/// </para>
/// </summary>
/// <param name="Source">
/// Which source won, and — when a located fixture lost — why. Surfaced to the user verbatim on a pairing
/// failure, so it is part of the contract rather than a debugging aid.
/// </param>
/// <param name="ContextKey">
/// The 16-byte field-cipher context key from the winning source. Empty when <see cref="Cipher"/> is
/// unavailable, so a caller that needs it checks the length rather than trusting availability alone.
/// </param>
public sealed record HalyardRegistrationCipherResolution(
    IHalyardRegistrationCipher Cipher,
    string Source,
    ReadOnlyMemory<byte> ContextKey);

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
    /// Resolve for <paramref name="platform"/>. The cipher is never null: when nothing can serve the family the
    /// result carries an unavailable cipher, so a caller checks
    /// <see cref="IHalyardRegistrationCipher.IsAvailable"/> rather than handling a null.
    /// </summary>
    HalyardRegistrationCipherResolution Resolve(HalyardConsolePlatform platform);
}
