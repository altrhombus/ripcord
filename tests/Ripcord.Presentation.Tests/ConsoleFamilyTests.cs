using Ripcord.Core;
using Ripcord.Presentation.Consoles;
using Xunit;

namespace Ripcord.Presentation.Tests;

/// <summary>
/// The console-family model: the boundary between the codename vocabulary Ripcord.Core speaks
/// (<see cref="ConsolePlatform.Halyard"/>) and the product names a user sees ("PlayStation 5").
///
/// <para>
/// The resolution rules matter more than they look. Each one has a default that keeps an existing install
/// working, and each default is silent when wrong: a record that fails to resolve does not error, it just shows
/// up as the wrong console.
/// </para>
/// </summary>
public class ConsoleFamilyTests
{
    [Fact]
    public void ForPlatformName_UnknownOrAbsent_IsPs5()
    {
        // Every record written before consoles.json carried a platform is a PS5, so an unrecognised or missing
        // value has to resolve to PS5 rather than to null or a throw. This mirrors
        // HalyardDiscoveryProfile.ForPlatformName deliberately — if the two disagreed, a console would be listed
        // as one family and probed as another.
        Assert.Equal(ConsoleFamily.Ps5, ConsoleFamily.ForPlatformName(null));
        Assert.Equal(ConsoleFamily.Ps5, ConsoleFamily.ForPlatformName(""));
        Assert.Equal(ConsoleFamily.Ps5, ConsoleFamily.ForPlatformName("garbage"));
    }

    [Theory]
    [InlineData("Ps5")]
    [InlineData("Ps4")]
    [InlineData("Lanyard")]
    public void ForPlatformName_RoundTripsEveryKey(string key)
    {
        // Key is what gets persisted, so every family must be able to read back what it wrote.
        Assert.Equal(key, ConsoleFamily.ForPlatformName(key).Key);
    }

    [Theory]
    [InlineData("ps4")]
    [InlineData("PS4")]
    [InlineData("pS4")]
    public void ForPlatformName_IsCaseInsensitive(string key)
        => Assert.Equal(ConsoleFamily.Ps4, ConsoleFamily.ForPlatformName(key));

    [Fact]
    public void ForHostType_MatchesWhatTheConsoleCallsItself()
    {
        // host-type is a wire value the console reports over SRCH. Believing it is how a PS4 discovered during a
        // PS5 scan still gets labelled as a PS4.
        Assert.Equal(ConsoleFamily.Ps5, ConsoleFamily.ForHostType("PS5"));
        Assert.Equal(ConsoleFamily.Ps4, ConsoleFamily.ForHostType("PS4"));
    }

    [Fact]
    public void ForHostType_UnknownIsNull_NotAGuess()
    {
        // Unlike ForPlatformName, there is no sensible default here: a host-type we do not recognise is a console
        // we do not support, and quietly calling it a PS5 would send it down the PS5 code path.
        Assert.Null(ConsoleFamily.ForHostType("XSX"));
        Assert.Null(ConsoleFamily.ForHostType(null));
    }

    [Theory]
    [InlineData(ConsolePlatform.Halyard, "Ps5")]
    [InlineData(ConsolePlatform.HalyardLegacy, "Ps4")]
    [InlineData(ConsolePlatform.Lanyard, "Lanyard")]
    public void ForPlatform_MapsEveryCodenameToItsProductName(ConsolePlatform platform, string expectedKey)
        => Assert.Equal(expectedKey, ConsoleFamily.ForPlatform(platform).Key);

    [Fact]
    public void PlayStationFamilies_ShareOneAccent_AndAreDistinguishedByName()
    {
        // A deliberate design decision recorded as a test: colour identifies the vendor, the caption identifies
        // the generation. Inventing a second blue to separate PS4 from PS5 would make the palette claim a
        // distinction the brand mark does not make.
        Assert.Equal(AccentRole.PlayStation, ConsoleFamily.Ps5.Accent);
        Assert.Equal(AccentRole.PlayStation, ConsoleFamily.Ps4.Accent);
        Assert.NotEqual(ConsoleFamily.Ps5.ShortName, ConsoleFamily.Ps4.ShortName);
    }

    [Fact]
    public void EachVendorHasItsOwnAccent()
    {
        Assert.Equal(AccentRole.Xbox, ConsoleFamily.Xbox.Accent);
        Assert.NotEqual(ConsoleFamily.Ps5.Accent, ConsoleFamily.Xbox.Accent);
    }

    [Fact]
    public void FullSupport_CarriesNoCaveat()
    {
        // A caveat on a family that just works would drown the ones that matter.
        Assert.Null(ConsoleFamily.Ps5.SupportNote);
        Assert.Null(ConsoleFamily.Ps5.SupportChip);
        Assert.Null(ConsoleFamily.Ps4.SupportNote);
    }

    [Fact]
    public void UnsupportedFamily_IsNotSelectable_AndSaysSo()
    {
        Assert.False(ConsoleFamily.Xbox.IsSelectable);
        Assert.NotNull(ConsoleFamily.Xbox.SupportNote);
        Assert.NotNull(ConsoleFamily.Xbox.SupportChip);
    }

    [Fact]
    public void Selectable_ExcludesWhatDoesNotWorkYet()
    {
        Assert.Contains(ConsoleFamily.Ps5, ConsoleFamily.Selectable);
        Assert.Contains(ConsoleFamily.Ps4, ConsoleFamily.Selectable);
        Assert.DoesNotContain(ConsoleFamily.Xbox, ConsoleFamily.Selectable);

        // Every selectable family must be paired-and-streamed reachable, or the picker offers a dead end.
        Assert.All(ConsoleFamily.Selectable, f => Assert.NotEqual(ConsoleFamilySupport.NotYetAvailable, f.Support));
    }

    [Fact]
    public void EveryFamilyKeyIsUnique()
    {
        // Keys are persisted identifiers; a duplicate would make ForPlatformName's first-match silently
        // authoritative over whichever family happened to be declared second.
        Assert.Equal(
            ConsoleFamily.All.Count,
            ConsoleFamily.All.Select(f => f.Key.ToLowerInvariant()).Distinct().Count());
    }
}
