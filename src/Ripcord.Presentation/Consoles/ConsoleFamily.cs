using Ripcord.Core;
using Ripcord.Presentation.Resources;

namespace Ripcord.Presentation.Consoles;

/// <summary>
/// The console maker a family belongs to. The three dashes of the Ripcord mark are one per vendor (blue
/// PlayStation, green Xbox, red Nintendo — see brand/README.md).
///
/// <para>
/// Colour identifies the <em>vendor</em>; the plain-text short name identifies the generation. PS4 and PS5
/// deliberately share the blue — they are the same vendor, and inventing a second blue to separate them
/// would make the palette say something the mark does not. The "PS5"/"PS4" caption is always shown and is
/// what tells them apart.
/// </para>
/// </summary>
public enum ConsoleVendor
{
    PlayStation,
    Xbox,
    Nintendo,
}

/// <summary>
/// How much of a family actually works, so the UI can be honest rather than aspirational. This is set from
/// what the project has really done, not what it intends to do.
/// </summary>
public enum ConsoleFamilySupport
{
    /// <summary>Verified end to end against real hardware.</summary>
    Full,

    /// <summary>Every layer is implemented and wire-confirmed, but the whole path has never been driven.</summary>
    Early,

    /// <summary>Named here so the shape of the app is legible, but nothing behind it exists yet.</summary>
    NotYetAvailable,
}

/// <summary>
/// The presentation model for a console family: what to call it, which accent it carries, and how far its
/// support actually goes.
///
/// <para>
/// This is the boundary between two vocabularies, which is why it is presentation rather than Core.
/// <c>Ripcord.Core</c> knows nothing about PlayStation — that is the whole reason
/// <see cref="ConsolePlatform"/> spells its members <c>Halyard</c>/<c>HalyardLegacy</c>/<c>Lanyard</c> instead
/// of using product names. Product names are a presentation concern and belong here.
/// </para>
///
/// <para>
/// Carries an <see cref="AccentRole"/> rather than a brush or a colour, so this type is linkable by a front end
/// that is not WinUI. Mapping the role to something drawable is each front end's job.
/// </para>
/// </summary>
/// <param name="Key">
/// The value persisted in <c>consoles.json</c>, matching <c>HalyardConsolePlatform.ToString()</c> for the
/// families that have one ("Ps5"/"Ps4"). Xbox uses the <see cref="ConsolePlatform.Lanyard"/> codename, since it
/// has no Halyard platform to name it with.
/// </param>
/// <param name="ShortName">The card caption — and the only thing distinguishing two families of one vendor.</param>
/// <param name="LongName">The full product name, for the family picker where there is room for it.</param>
public sealed record ConsoleFamily(
    string Key,
    string ShortName,
    string LongName,
    ConsoleVendor Vendor,
    ConsoleFamilySupport Support)
{
    /// <summary>PS5 — live end to end: pairing, connect, video, audio and input all verified on hardware.</summary>
    public static readonly ConsoleFamily Ps5 =
        new("Ps5", "PS5", "PlayStation 5", ConsoleVendor.PlayStation, ConsoleFamilySupport.Full);

    /// <summary>
    /// PS4 — full as of 2026-08-05. Promoted from "Early" that day, when the last open item was closed: a
    /// pairing-from-scratch run against a real PS4 succeeded from the UI, and a connect test streamed it. Until
    /// then this said "a PS4 hasn't been streamed end to end yet", which was true when written and had quietly
    /// stopped being true — the caveat is what the user sees, so it has to track reality.
    /// </summary>
    public static readonly ConsoleFamily Ps4 =
        new("Ps4", "PS4", "PlayStation 4", ConsoleVendor.PlayStation, ConsoleFamilySupport.Full);

    /// <summary>
    /// Xbox — present so the three-family shape of the app is visible from the first screen, but there is no
    /// protocol project, discovery service or session factory behind it. Offering it and then failing would
    /// be worse than saying so.
    /// </summary>
    public static readonly ConsoleFamily Xbox =
        new(nameof(ConsolePlatform.Lanyard), "Xbox", "Xbox", ConsoleVendor.Xbox, ConsoleFamilySupport.NotYetAvailable);

    /// <summary>Every family the app knows about, in the order a picker would offer them.</summary>
    public static readonly IReadOnlyList<ConsoleFamily> All = [Ps5, Ps4, Xbox];

    /// <summary>The families that can actually be paired and streamed today.</summary>
    public static IReadOnlyList<ConsoleFamily> Selectable { get; } = [.. All.Where(f => f.IsSelectable)];

    /// <summary>True when this family can actually be paired and streamed today.</summary>
    public bool IsSelectable => Support != ConsoleFamilySupport.NotYetAvailable;

    /// <summary>The accent slot this family draws in. Vendor identity, not generation.</summary>
    public AccentRole Accent => Vendor switch
    {
        ConsoleVendor.Xbox => AccentRole.Xbox,
        ConsoleVendor.Nintendo => AccentRole.Nintendo,
        _ => AccentRole.PlayStation,
    };

    /// <summary>
    /// The short note shown beside a family that is not fully proven. Null for
    /// <see cref="ConsoleFamilySupport.Full"/> — a family that just works needs no caveat, and adding one to
    /// every card would drown the ones that matter.
    /// </summary>
    public string? SupportNote => Support switch
    {
        ConsoleFamilySupport.Early => Strings.Family_EarlySupportNote,
        ConsoleFamilySupport.NotYetAvailable => Strings.Family_NotYetAvailableNote,
        _ => null,
    };

    /// <summary>The chip caption beside the family name, or null when there is nothing to qualify.</summary>
    public string? SupportChip => Support switch
    {
        ConsoleFamilySupport.Early => Strings.Family_EarlySupportChip,
        ConsoleFamilySupport.NotYetAvailable => Strings.Family_NotYetAvailableChip,
        _ => null,
    };

    /// <summary>
    /// Resolve a persisted platform string to its family, mirroring
    /// <c>HalyardDiscoveryProfile.ForPlatformName</c> so the two agree on what an unknown value means: an
    /// absent or unrecognised platform is PS5, which is what every pre-existing record is.
    /// </summary>
    public static ConsoleFamily ForPlatformName(string? platform) =>
        All.FirstOrDefault(f => string.Equals(f.Key, platform, StringComparison.OrdinalIgnoreCase)) ?? Ps5;

    /// <summary>
    /// Resolve the <c>host-type</c> a console reports over SRCH ("PS5"/"PS4") to a family. This is a wire
    /// value, not a display string — the console tells us what it is and we believe it, which is how a PS4
    /// found during a PS5 scan still gets labelled correctly.
    /// </summary>
    public static ConsoleFamily? ForHostType(string? hostType) =>
        All.FirstOrDefault(f => string.Equals(f.ShortName, hostType, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolve Core's platform codename to a family. Core deliberately does not know product names, so this
    /// mapping — codename to marketing name — is the boundary between the two vocabularies, and belongs here.
    /// </summary>
    public static ConsoleFamily ForPlatform(ConsolePlatform platform) => platform switch
    {
        ConsolePlatform.HalyardLegacy => Ps4,
        ConsolePlatform.Lanyard => Xbox,
        _ => Ps5,
    };
}
