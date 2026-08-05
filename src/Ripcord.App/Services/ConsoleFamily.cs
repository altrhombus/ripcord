using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Ripcord_App.Services;

/// <summary>
/// The console maker a family belongs to. Carries the accent colour, and nothing else: the three dashes of
/// the Ripcord mark are one per vendor (blue PlayStation, green Xbox, red Nintendo — see brand/README.md).
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
/// This lives in the app layer rather than <c>Ripcord.Core</c> on purpose. Core knows nothing about
/// PlayStation — that is the whole reason <c>ConsolePlatform</c> spells its members
/// <c>Halyard</c>/<c>HalyardLegacy</c>/<c>Lanyard</c> instead of using product names. Product names are a
/// presentation concern and belong up here, where the About page already puts them.
/// </para>
/// </summary>
/// <param name="Key">
/// The value persisted in <c>consoles.json</c>, matching <c>HalyardConsolePlatform.ToString()</c> for the
/// families that have one ("Ps5"/"Ps4"). Xbox uses the <c>ConsolePlatform.Lanyard</c> codename, since it has
/// no Halyard platform to name it with.
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
    /// PS4 — discovery, wake, registration and session crypto are all implemented and wire-confirmed against
    /// a real console, but no PS4 has been driven all the way to a stream. "Early", not "Full", until one has.
    /// </summary>
    public static readonly ConsoleFamily Ps4 =
        new("Ps4", "PS4", "PlayStation 4", ConsoleVendor.PlayStation, ConsoleFamilySupport.Early);

    /// <summary>
    /// Xbox — present so the three-family shape of the app is visible from the first screen, but there is no
    /// protocol project, discovery service or session factory behind it. Offering it and then failing would
    /// be worse than saying so.
    /// </summary>
    public static readonly ConsoleFamily Xbox =
        new(nameof(Ripcord.Core.ConsolePlatform.Lanyard), "Xbox", "Xbox", ConsoleVendor.Xbox, ConsoleFamilySupport.NotYetAvailable);

    /// <summary>Every family the picker offers, in the order it offers them.</summary>
    public static readonly IReadOnlyList<ConsoleFamily> All = [Ps5, Ps4, Xbox];

    /// <summary>True when this family can actually be paired and streamed today.</summary>
    public bool IsSelectable => Support != ConsoleFamilySupport.NotYetAvailable;

    /// <summary>
    /// The short note shown beside a family that is not fully proven. Null for <see cref="ConsoleFamilySupport.Full"/>
    /// — a family that just works needs no caveat, and adding one to every card would drown the two that matter.
    /// </summary>
    public string? SupportNote => Support switch
    {
        ConsoleFamilySupport.Early =>
            "Everything needed is built and confirmed against a real console, but a PS4 hasn't been streamed "
            + "end to end yet. Expect rough edges.",
        ConsoleFamilySupport.NotYetAvailable =>
            "Xbox isn't supported yet. It's here so you can see where Ripcord is going.",
        _ => null,
    };

    /// <summary>The chip caption beside the family name, or null when there is nothing to qualify.</summary>
    public string? SupportChip => Support switch
    {
        ConsoleFamilySupport.Early => "Early support",
        ConsoleFamilySupport.NotYetAvailable => "Not yet available",
        _ => null,
    };

    /// <summary>
    /// The vendor's accent, resolved from the app resources. Looked up by key rather than held as a
    /// <c>Color</c> here so the palette has exactly one home — <c>Styles/Ripcord.xaml</c>, which in turn
    /// points at brand/README.md. Same idiom as <see cref="ConsoleListItem.StatusBrush"/>.
    /// </summary>
    public Brush AccentBrush => (Brush)Application.Current.Resources[Vendor switch
    {
        ConsoleVendor.Xbox => "RipcordXboxAccentBrush",
        ConsoleVendor.Nintendo => "RipcordNintendoAccentBrush",
        _ => "RipcordPlayStationAccentBrush",
    }];

    /// <summary>The same accent as a bare colour, for gradient stops (which cannot take a brush).</summary>
    public Windows.UI.Color AccentColor => (Windows.UI.Color)Application.Current.Resources[Vendor switch
    {
        ConsoleVendor.Xbox => "RipcordXboxAccentColor",
        ConsoleVendor.Nintendo => "RipcordNintendoAccentColor",
        _ => "RipcordPlayStationAccentColor",
    }];

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
    public static ConsoleFamily ForPlatform(Ripcord.Core.ConsolePlatform platform) => platform switch
    {
        Ripcord.Core.ConsolePlatform.HalyardLegacy => Ps4,
        Ripcord.Core.ConsolePlatform.Lanyard => Xbox,
        _ => Ps5,
    };
}
