using Ripcord.Core.Input;
using Ripcord.Presentation.Halyard.Sessions;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>The console's passcode buttons, read from the pad as the console reads them.</summary>
public class HalyardPasscodePadTests
{
    private static ControllerStateFrame Frame(
        ControllerButtons buttons = ControllerButtons.None, float leftTrigger = 0, float rightTrigger = 0)
        => new(0, buttons, 0, 0, 0, 0, leftTrigger, rightTrigger, null, null, null);

    private static HalyardPasscodePad Primed() => new();

    [Theory]
    [InlineData(ControllerButtons.DPadLeft, '1')]
    [InlineData(ControllerButtons.DPadUp, '2')]
    [InlineData(ControllerButtons.DPadRight, '3')]
    [InlineData(ControllerButtons.DPadDown, '4')]
    [InlineData(ControllerButtons.RightShoulder, '5')]
    [InlineData(ControllerButtons.LeftShoulder, '7')]
    [InlineData(ControllerButtons.North, '9')]
    [InlineData(ControllerButtons.West, '0')]
    public void EachButton_IsTheConsolesDigit(ControllerButtons button, char digit)
    {
        HalyardPasscodePad pad = Primed();

        Assert.Equal([new PasscodePadInput(PasscodePadAction.Digit, digit)], pad.Read(Frame(button)));
    }

    [Fact]
    public void TheTriggers_AreSixAndEight()
    {
        HalyardPasscodePad pad = Primed();

        Assert.Equal([new PasscodePadInput(PasscodePadAction.Digit, '6')], pad.Read(Frame(rightTrigger: 1)));
        Assert.Equal([new PasscodePadInput(PasscodePadAction.Digit, '8')], pad.Read(Frame(leftTrigger: 1, rightTrigger: 1)));
    }

    [Fact]
    public void CrossAndCircle_AreTheTwoActions()
    {
        HalyardPasscodePad pad = Primed();

        Assert.Equal([new PasscodePadInput(PasscodePadAction.Submit)], pad.Read(Frame(ControllerButtons.South)));
        Assert.Equal([new PasscodePadInput(PasscodePadAction.Back)], pad.Read(Frame(ControllerButtons.East)));
    }

    [Fact]
    public void AHeldButton_IsOneDigit()
    {
        HalyardPasscodePad pad = Primed();

        Assert.Single(pad.Read(Frame(ControllerButtons.DPadUp)));
        Assert.Empty(pad.Read(Frame(ControllerButtons.DPadUp)));
        Assert.Empty(pad.Read(Frame()));
        Assert.Single(pad.Read(Frame(ControllerButtons.DPadUp)));
    }

    [Fact]
    public void WhatWasHeldWhenThePromptOpened_IsNotAPress()
    {
        // Cross is what opened the connect, and may still be down when the prompt appears.
        var pad = new HalyardPasscodePad(Frame(ControllerButtons.South, rightTrigger: 1));

        Assert.Empty(pad.Read(Frame(ControllerButtons.South, rightTrigger: 1)));
        Assert.Empty(pad.Read(Frame(ControllerButtons.South)));
        Assert.Empty(pad.Read(Frame()));
        Assert.Equal([new PasscodePadInput(PasscodePadAction.Submit)], pad.Read(Frame(ControllerButtons.South)));
    }

    [Fact]
    public void TheFirstFrame_CanBeAPress()
    {
        // A source that publishes only on change sends nothing until something is pressed.
        var pad = new HalyardPasscodePad(Frame());

        Assert.Equal([new PasscodePadInput(PasscodePadAction.Digit, '2')], pad.Read(Frame(ControllerButtons.DPadUp)));
    }

    [Fact]
    public void ATriggerNearTheLine_TypesOnce()
    {
        HalyardPasscodePad pad = Primed();

        Assert.Single(pad.Read(Frame(rightTrigger: HalyardPasscodePad.TriggerPress)));

        // Wavering between the two thresholds is still one press.
        Assert.Empty(pad.Read(Frame(rightTrigger: 0.4f)));
        Assert.Empty(pad.Read(Frame(rightTrigger: 0.6f)));
        Assert.Empty(pad.Read(Frame(rightTrigger: 0.35f)));

        Assert.Empty(pad.Read(Frame(rightTrigger: 0.1f)));
        Assert.Single(pad.Read(Frame(rightTrigger: 0.9f)));
    }

    [Fact]
    public void TheLegend_ListsOneToNineThenZero_AndEachKeyTypesItsDigit()
    {
        Assert.Equal("1234567890", string.Concat(HalyardPasscodePad.Keys.Select(k => k.Digit)));

        foreach (PasscodeKey key in HalyardPasscodePad.Keys)
        {
            ControllerStateFrame press = key.Trigger switch
            {
                PasscodeTrigger.Left => Frame(leftTrigger: 1),
                PasscodeTrigger.Right => Frame(rightTrigger: 1),
                _ => Frame(key.Button),
            };

            Assert.Equal([new PasscodePadInput(PasscodePadAction.Digit, key.Digit)], Primed().Read(press));
        }
    }

    [Theory]
    [InlineData(PadFamily.Vendor, "← ↑ → ↓ R1 R2 L1 L2 Triangle Square")]
    [InlineData(PadFamily.Generic, "← ↑ → ↓ RB RT LB LT Y X")]
    public void TheLegend_NamesThePadInHand(PadFamily family, string names)
        => Assert.Equal(names, string.Join(' ', HalyardPasscodePad.Keys.Select(k => k.NameFor(family))));

    [Fact]
    public void NothingElse_IsADigit()
    {
        HalyardPasscodePad pad = Primed();

        Assert.Empty(pad.Read(Frame(
            ControllerButtons.Start | ControllerButtons.Select | ControllerButtons.Guide
            | ControllerButtons.LeftStick | ControllerButtons.RightStick | ControllerButtons.TouchpadClick)));
    }
}
