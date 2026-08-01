using Ripcord.Core.Input;
using Xunit;

namespace Ripcord.Protocol.Halyard.Tests;

/// <summary>
/// Keyboard-to-gamepad translation, binding edits, and source merging.
///
/// <para>
/// All of this is pure state over "which keys are held", which is the point: the failure modes here are a stuck
/// stick and an inescapable stream, neither of which is pleasant to discover on hardware.
/// </para>
/// </summary>
public class KeyboardInputTests
{
    private static KeyboardInputTranslator Translator(InputBindings? bindings = null)
        => new(bindings ?? new InputBindings());

    // ---- axis translation ----

    [Fact]
    public void HoldingADirectionDeflectsThatAxisFully()
    {
        var t = Translator();
        t.KeyDown(VirtualKeys.W);

        ControllerStateFrame f = t.ToFrame(0);
        Assert.Equal(1f, f.LeftStickY);
        Assert.Equal(0f, f.LeftStickX);
    }

    [Fact]
    public void OpposingDirectionsCancelToCentre()
    {
        // The bug this prevents: if directions overrode instead of summing, whichever key the held-set enumerated
        // last would win, and the stick would appear stuck at full deflection while both keys were down.
        var t = Translator();
        t.KeyDown(VirtualKeys.A);
        t.KeyDown(VirtualKeys.D);

        Assert.Equal(0f, t.ToFrame(0).LeftStickX);
    }

    [Fact]
    public void DiagonalsAreClampedToTheUnitCircle()
    {
        // Two keys give (1,1), magnitude 1.41. Left as-is, diagonal movement is ~41% faster than cardinal — the
        // classic keyboard-driving artefact.
        var t = Translator();
        t.KeyDown(VirtualKeys.W);
        t.KeyDown(VirtualKeys.D);

        ControllerStateFrame f = t.ToFrame(0);
        float magnitude = MathF.Sqrt((f.LeftStickX * f.LeftStickX) + (f.LeftStickY * f.LeftStickY));
        Assert.Equal(1f, magnitude, 3);
        Assert.Equal(f.LeftStickX, f.LeftStickY, 3);
    }

    [Fact]
    public void TheTwoSticksAreIndependent()
    {
        var t = Translator();
        t.KeyDown(VirtualKeys.W);
        t.KeyDown(VirtualKeys.Left);

        ControllerStateFrame f = t.ToFrame(0);
        Assert.Equal(1f, f.LeftStickY);
        Assert.Equal(-1f, f.RightStickX);
        Assert.Equal(0f, f.LeftStickX);
        Assert.Equal(0f, f.RightStickY);
    }

    [Fact]
    public void TriggersAndButtonsCoexist()
    {
        var t = Translator();
        t.KeyDown(VirtualKeys.Number3);  // L2
        t.KeyDown(VirtualKeys.Space);    // Cross

        ControllerStateFrame f = t.ToFrame(0);
        Assert.Equal(1f, f.LeftTrigger);
        Assert.Equal(ControllerButtons.South, f.Buttons & ControllerButtons.South);
    }

    // ---- held-state hygiene ----

    [Fact]
    public void ReleasingAKeyReleasesItsInput()
    {
        var t = Translator();
        t.KeyDown(VirtualKeys.W);
        t.KeyUp(VirtualKeys.W);

        Assert.Equal(0f, t.ToFrame(0).LeftStickY);
        Assert.False(t.HasInput);
    }

    [Fact]
    public void ClearReleasesEverything()
    {
        // Focus loss stops key-up delivery, so without Clear a key held at alt-tab stays held forever: permanent
        // full deflection into the game that the user cannot correct without reconnecting.
        var t = Translator();
        t.KeyDown(VirtualKeys.W);
        t.KeyDown(VirtualKeys.Space);

        t.Clear();

        ControllerStateFrame f = t.ToFrame(0);
        Assert.Equal(0f, f.LeftStickY);
        Assert.Equal(ControllerButtons.None, f.Buttons);
        Assert.False(t.HasInput);
    }

    [Fact]
    public void RepeatedKeyDownIsIdempotentAndReportsNoChange()
    {
        // Auto-repeat fires KeyDown continuously; each one must not count as a new event or the session would be
        // spammed with identical frames.
        var t = Translator();
        Assert.True(t.KeyDown(VirtualKeys.W));
        Assert.False(t.KeyDown(VirtualKeys.W));
    }

    [Fact]
    public void UnboundKeysAreIgnoredEntirely()
    {
        var t = Translator();
        Assert.False(t.KeyDown(VirtualKeys.F3));
        Assert.False(t.HasInput);
        Assert.Equal(ControllerButtons.None, t.ToFrame(0).Buttons);
    }

    [Fact]
    public void ChangingBindingsWhileAKeyIsHeldDoesNotStrandTheOldAction()
    {
        var t = Translator();
        t.KeyDown(VirtualKeys.W);
        Assert.Equal(1f, t.ToFrame(0).LeftStickY);

        t.Bindings = new InputBindings { Keyboard = new Dictionary<int, InputAction>() };

        Assert.Equal(0f, t.ToFrame(0).LeftStickY);
        Assert.False(t.HasInput);
    }

    // ---- binding edits ----

    [Fact]
    public void ReservedKeysCannotBeBound()
    {
        // Binding Escape would make a fullscreen stream inescapable on a keyboard-only machine — the same trap
        // the controller exit gesture exists to prevent.
        var bindings = new InputBindings();

        foreach (int reserved in InputBindings.ReservedKeys)
        {
            Assert.False(InputBindings.IsBindable(reserved));
            Assert.Same(bindings, bindings.WithKey(reserved, InputAction.South));
        }
    }

    [Fact]
    public void KeyCodeZeroIsNotBindable()
    {
        // An unrecognised key arrives as 0; binding it would map every unknown key to one action.
        Assert.False(InputBindings.IsBindable(0));
    }

    [Fact]
    public void RebindingAKeyClearsItsPreviousAction()
    {
        InputBindings b = new InputBindings().WithKey(VirtualKeys.W, InputAction.North);

        Assert.Equal(InputAction.North, b.Keyboard[VirtualKeys.W]);
        Assert.DoesNotContain(VirtualKeys.W, b.KeysFor(InputAction.LeftStickUp));
    }

    [Fact]
    public void OneActionMayHaveSeveralKeys()
    {
        InputBindings b = new InputBindings().WithKey(VirtualKeys.Number1, InputAction.South);

        Assert.Contains(VirtualKeys.Space, b.KeysFor(InputAction.South));
        Assert.Contains(VirtualKeys.Number1, b.KeysFor(InputAction.South));
    }

    [Fact]
    public void BindingToNoneUnbindsTheKey()
    {
        InputBindings b = new InputBindings().WithKey(VirtualKeys.W, InputAction.None);
        Assert.DoesNotContain(VirtualKeys.W, (IEnumerable<int>)b.Keyboard.Keys.ToList());
    }

    [Fact]
    public void WithoutActionClearsEveryKeyForIt()
    {
        InputBindings b = new InputBindings()
            .WithKey(VirtualKeys.Number1, InputAction.South)
            .WithoutAction(InputAction.South);

        Assert.Empty(b.KeysFor(InputAction.South));
    }

    [Fact]
    public void EditsDoNotMutateTheOriginal()
    {
        var original = new InputBindings();
        original.WithKey(VirtualKeys.W, InputAction.North);

        Assert.Equal(InputAction.LeftStickUp, original.Keyboard[VirtualKeys.W]);
    }

    [Fact]
    public void TheDefaultLayoutBindsNothingReserved()
    {
        foreach (int key in InputBindings.DefaultKeyboard.Keys)
        {
            Assert.True(InputBindings.IsBindable(key), $"default layout binds reserved key 0x{key:X2}");
        }
    }

    [Fact]
    public void KeyboardIsOffByDefault()
        => Assert.False(new InputBindings().KeyboardEnabled);

    // ---- value equality ----

    [Fact]
    public void BindingsWithEqualContentsAreEqual()
    {
        // Hand-written equality, so it needs pinning: a record would compare its dictionary members by REFERENCE,
        // making two identical bindings unequal and every settings load look like a change.
        var a = new InputBindings().WithKey(VirtualKeys.Number1, InputAction.North);
        var b = new InputBindings().WithKey(VirtualKeys.Number1, InputAction.North);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void BindingsDifferingInAnyFieldAreNotEqual()
    {
        var baseline = new InputBindings();

        Assert.NotEqual(baseline, baseline with { KeyboardEnabled = true });
        Assert.NotEqual(baseline, baseline.WithKey(VirtualKeys.Number1, InputAction.North));
        Assert.NotEqual(baseline, baseline.WithoutAction(InputAction.South));
        Assert.NotEqual(
            baseline,
            baseline with
            {
                GamepadRemap = new Dictionary<ControllerButtons, ControllerButtons>
                {
                    [ControllerButtons.South] = ControllerButtons.East,
                },
            });
    }

    [Fact]
    public void EqualityDoesNotDependOnInsertionOrder()
    {
        var a = new InputBindings { Keyboard = new Dictionary<int, InputAction>
        {
            [VirtualKeys.A] = InputAction.South,
            [VirtualKeys.C] = InputAction.North,
        } };
        var b = new InputBindings { Keyboard = new Dictionary<int, InputAction>
        {
            [VirtualKeys.C] = InputAction.North,
            [VirtualKeys.A] = InputAction.South,
        } };

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    // ---- gamepad remap ----

    [Fact]
    public void RemapRelabelsOnlyWhatItNames()
    {
        var remap = new Dictionary<ControllerButtons, ControllerButtons>
        {
            [ControllerButtons.South] = ControllerButtons.East,
        };

        ControllerStateFrame f = Frame(ControllerButtons.South | ControllerButtons.North);
        ControllerStateFrame result = ControllerInputMerger.ApplyRemap(f, remap);

        Assert.Equal(ControllerButtons.East | ControllerButtons.North, result.Buttons);
    }

    [Fact]
    public void RemapCanSwapTwoButtons()
    {
        // Requires reading from the original frame per bit; a naive in-place rewrite has one overwrite the other.
        var remap = new Dictionary<ControllerButtons, ControllerButtons>
        {
            [ControllerButtons.South] = ControllerButtons.East,
            [ControllerButtons.East] = ControllerButtons.South,
        };

        Assert.Equal(
            ControllerButtons.South | ControllerButtons.East,
            ControllerInputMerger.ApplyRemap(Frame(ControllerButtons.South | ControllerButtons.East), remap).Buttons);

        Assert.Equal(
            ControllerButtons.East,
            ControllerInputMerger.ApplyRemap(Frame(ControllerButtons.South), remap).Buttons);
    }

    [Fact]
    public void AnEmptyRemapIsAPassthrough()
    {
        ControllerStateFrame f = Frame(ControllerButtons.North);
        Assert.Equal(f, ControllerInputMerger.ApplyRemap(f, new Dictionary<ControllerButtons, ControllerButtons>()));
    }

    // ---- on-screen (virtual) buttons ----

    [Fact]
    public void VirtualButtonsAreAddedNotSubstituted()
    {
        // The case that matters: tapping the on-screen PS button while a stick is held must send BOTH. These buttons
        // exist because an Xbox pad cannot send them at all — Windows claims its Guide button for the shell — so
        // replacing the frame instead of adding to it would trade one unreachable input for another.
        ControllerStateFrame held = Frame(ControllerButtons.South) with { LeftStickY = 1f };

        ControllerStateFrame result = ControllerInputMerger.WithVirtualButtons(held, ControllerButtons.Guide);

        Assert.Equal(ControllerButtons.South | ControllerButtons.Guide, result.Buttons);
        Assert.Equal(1f, result.LeftStickY);
    }

    [Fact]
    public void NoVirtualButtonsLeavesTheFrameUntouched()
    {
        ControllerStateFrame f = Frame(ControllerButtons.North) with { RightTrigger = 0.5f };
        Assert.Equal(f, ControllerInputMerger.WithVirtualButtons(f, ControllerButtons.None));
    }

    [Fact]
    public void SeveralVirtualButtonsCanBeHeldTogether()
    {
        ControllerStateFrame result = ControllerInputMerger.WithVirtualButtons(
            Frame(), ControllerButtons.Guide | ControllerButtons.TouchpadClick);

        Assert.Equal(ControllerButtons.Guide | ControllerButtons.TouchpadClick, result.Buttons);
    }

    [Fact]
    public void AVirtualButtonAlreadyHeldOnThePadIsNotDoubleCounted()
    {
        // OR is idempotent, which is what makes the on-screen Options button safe to press while the pad's own
        // Options is down — and what makes releasing one not cancel the other.
        ControllerStateFrame result = ControllerInputMerger.WithVirtualButtons(
            Frame(ControllerButtons.Start), ControllerButtons.Start);

        Assert.Equal(ControllerButtons.Start, result.Buttons);
    }

    // ---- merging ----

    [Fact]
    public void ARestingPadDoesNotCancelHeldKeys()
    {
        // The whole reason merging exists. Frames are absolute state, so forwarding whichever source spoke last
        // would have the pad's resting frame cancel a held key at whatever rate the pad reports.
        ControllerStateFrame pad = Frame(ControllerButtons.None);
        ControllerStateFrame keys = Frame(ControllerButtons.South) with { LeftStickY = 1f };

        ControllerStateFrame merged = ControllerInputMerger.Merge(pad, keys);

        Assert.Equal(ControllerButtons.South, merged.Buttons);
        Assert.Equal(1f, merged.LeftStickY);
    }

    [Fact]
    public void MergeTakesWhicheverAxisIsFurtherFromCentre()
    {
        ControllerStateFrame pad = Frame() with { LeftStickX = 0.4f };
        ControllerStateFrame keys = Frame() with { LeftStickX = -1f };

        Assert.Equal(-1f, ControllerInputMerger.Merge(pad, keys).LeftStickX);
        Assert.Equal(-1f, ControllerInputMerger.Merge(keys, pad).LeftStickX);
    }

    [Fact]
    public void MergeIsOrderIndependentForButtonsAndTriggers()
    {
        ControllerStateFrame a = Frame(ControllerButtons.South) with { LeftTrigger = 0.5f };
        ControllerStateFrame b = Frame(ControllerButtons.North) with { LeftTrigger = 1f };

        ControllerStateFrame ab = ControllerInputMerger.Merge(a, b);
        ControllerStateFrame ba = ControllerInputMerger.Merge(b, a);

        Assert.Equal(ab.Buttons, ba.Buttons);
        Assert.Equal(ab.LeftTrigger, ba.LeftTrigger);
        Assert.Equal(1f, ab.LeftTrigger);
    }

    [Fact]
    public void MergeKeepsMotionAndTouchFromWhicheverSourceHasThem()
    {
        // A keyboard has no gyro; the pad's must survive the merge regardless of argument order.
        ControllerStateFrame pad = Frame() with
        {
            Gyro = new GyroSample(1, 2, 3),
            Touchpad = new TouchpadSample(0.5f, 0.5f, true),
        };
        ControllerStateFrame keys = Frame();

        Assert.NotNull(ControllerInputMerger.Merge(keys, pad).Gyro);
        Assert.NotNull(ControllerInputMerger.Merge(pad, keys).Gyro);
        Assert.True(ControllerInputMerger.Merge(keys, pad).Touchpad!.Value.Touching);
    }

    [Fact]
    public void MergeTakesTheLaterTimestamp()
    {
        Assert.Equal(
            50,
            ControllerInputMerger.Merge(Frame() with { TimestampTicks = 10 }, Frame() with { TimestampTicks = 50 })
                .TimestampTicks);
    }

    private static ControllerStateFrame Frame(ControllerButtons buttons = ControllerButtons.None)
        => new(0, buttons, 0, 0, 0, 0, 0, 0, null, null, null);
}
