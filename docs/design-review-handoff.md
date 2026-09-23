# Design review — handoff to the Windows environment

**The question this answers: the portable half is done and green. What is left, and exactly how?**

Written 2026-09-21 from WSL, where the WinUI half cannot be built at all — `Ripcord.App`,
`Ripcord.Media.Interop` and `Ripcord.Input.Interop` need MSBuild and a Windows SDK. Everything in
`Ripcord.Core` and `Ripcord.Presentation` *is* buildable and testable here, so it was done here and verified.

Companion to [`design-review-2026-09-21.md`](design-review-2026-09-21.md), which carries the reasoning. This
file carries the instructions. Where the two disagree, the amendments in
[§0](#0-three-decisions-taken-after-the-review) win — they were settled after the review was written.

Delete this file once the work has landed and its findings are in [`../ROADMAP.md`](../ROADMAP.md).

---

## Status

**Done here, on this branch, both suites green.**

```
dotnet test tests/Ripcord.Presentation.Tests/...   411 passed,   0 failed
dotnet test tests/Ripcord.Protocol.Halyard.Tests/  787 passed,   0 failed,  7 skipped
```

The seven skips are environmental and expected on a Linux x64 host — four ARM PMULL paths, one Windows-only
device-identity test, one cipher-availability test. None are the dirty-room live-vector tests.

| Landed | What it is |
|---|---|
| `Ripcord.Presentation/Consoles/CardMetrics.cs` | The grid's layout rule: density, cell, columns, add-tile. Pure. |
| `Ripcord.Presentation/Sessions/ConnectGate.cs` | The connect dwell rule (C1). Pure, clock-driven. |
| `SessionViewState.AlertHint` + `SessionViewModel.SetInputMode` | D2: rung 1 stops naming a key the player has not got. |
| `SessionViewState.HealthNotice` | The rung-1 line, verdict plus one actionable clause. |
| `StreamHealthVerdict.Remedy` / `.Notice` | The clause itself, on all eight raisable verdicts. |
| `AlertVisible` now requires `Rung == Hidden` | The double-verdict defect found during this pass. |
| `CardMetricsTests`, `ConnectGateTests`, `HudNoticeTests` | 33 new tests. |

**Everything below needs Windows.** Nothing below has been compiled.

```
msbuild Ripcord.slnx -p:Platform=ARM64 -p:Configuration=Release -m
```

---

## 0. Three decisions taken after the review

### Note 1 — the hero becomes a grid of one

The review proposed one shared `ConsoleCard` control with a `Density` property. **That is superseded.** The
better answer, and the one chosen: the `GridView` always renders, and at one console the cell is hero-sized.

The reason it works is that `ConsoleCardState` already carries every fact `RenderHero()` sets imperatively —
`PrimaryActionLabel`, `LastConnectedLabel`, `IsChecking`, `StatusTone`, `Accent`, `AutomationName`. The hero
needed code-behind **only** because it was not inside an items control. Put it in one and the code has
nothing left to do.

So divergence stops being discouraged and becomes unrepresentable, which is the point — the reported symptom
was "making a change but it only applied to one of the views", and a rule that can be broken will be.

### Note 2 — the family step is repositioned, not removed

The review said to delete the "Which console are you connecting to?" step. **Softened, because Xbox work is
starting soon.** The step survives; what changes is when it is asked and where the buttons come from.

- **Discovery leads.** For any console found on the network the platform is already reported, so the question
  answers itself and the step is skipped.
- **The step appears only on the manual-address path**, and automatically when discovery finds nothing —
  which is exactly where the family genuinely is not knowable.
- **The buttons are built from a list of implemented platforms** rather than hard-coded in XAML. Xbox then
  appears the moment `ConsolePlatform.Lanyard` is implemented, and not before. That is strictly better for
  the Xbox plan than today's markup: when it lands, nothing in this page changes.
- **When Lanyard lands, `IConsoleScanner` becomes a composite** that fans out to both backends and merges —
  the same shape as the composite controller source. That is what keeps "discovery leads" true for two
  protocols, and it is worth knowing now because it is the thing that would otherwise force the family
  question back to the front.

The only defect that must not ship is a *dead* Xbox button, and availability-gating fixes that without
deleting a line of the work.

### Note 3 — a reserved touch shelf

**Confirmed on hardware and on the desktop.** The arithmetic:

| | Margin | Occupies (from bottom) |
|---|---|---|
| `TouchControls` | `0,0,0,20`, 52 px buttons + 8 padding + border | 20 – 90 |
| `DiagnosticsSummary` (rung 2) | `28,0,28,28`, full width | 28 – 84 |
| `HealthAlert` (rung 1) | `28,0,0,28`, left, `MaxWidth=400` | 28 – ~70 |
| `ExitProgressPanel` | `0,0,0,104` | 104 – ~150 |

Rung 2 and the touch bar overlap almost exactly. It hits the desktop as well as the handheld because
`StreamSurface_PointerMoved` summons the touch bar on mouse movement. And that `104` is the tell — someone
already hand-computed a clearance number for this collision, once, in one place, in a literal.

**The fix is layout, not arithmetic.** One bottom-anchored `Grid`, two rows, the shelf reserved whether or
not the bar is showing, so nothing moves when it times out after four seconds.

---

## 1. The console card — one template, three densities

**Files:** `Pages/ConsolesPage.xaml`, `Pages/ConsolesPage.xaml.cs`, `Styles/Ripcord.Card.xaml`

### 1.1 Delete

- The entire `HeroPanel` `StackPanel` and everything inside it (`HeroCard`, `HeroWedge`, `HeroName`,
  `HeroMark`, `HeroDetails`, `HeroStatusDot`, `HeroStatus`, `HeroLastPlayed`, `HeroPlayLabel`,
  `HeroOverflowButton`, `HeroHoverWash`, `HeroAddLink`).
- `AttachHero`, `DetachHero`, `RenderHero`, `OnHeroChanged`, `OnHeroHighlight`, `OnHeroUnhighlight`,
  `OnHeroPlayClick`, `OnHeroOverflowClick`, `OnHeroContextRequested`, and the `_heroConsole` field.
- `RipcordCardButtonStyle` in `Ripcord.Card.xaml`. It existed solely because the hero had no focusable
  container; the `GridViewItem` is that container again.
- The `HeroPanel.Visibility == Visibility.Visible ? HeroCard : ...` branch in `PrepareConnectAnimation`.
  Every card is a `GridViewItem` now, so the source is always `ContainerFromItem`.

### 1.2 The template gains three density-conditional pieces

`ConsoleCardTemplate` stays one `DataTemplate`. What varies:

| | Grid (280×176) | Roomy (348×220) | Hero (520×176) |
|---|---|---|---|
| Wedge width | **72** (was 92) | 112 | 184 |
| Name style | `SubtitleTextBlockStyle` | `SubtitleTextBlockStyle` | `TitleTextBlockStyle` |
| Content margin | `16,14,84,14` | `20,18,124,18` | `28,24,200,24` |
| `PrimaryActionLabel` over the wedge | hidden | hidden | **shown** |
| Status and last-played | two rows | two rows | **one row** |

Drive it from a `CardDensity` on the view-model (set by the page from `CardMetrics.For`), through
converters. Three `DataTemplate`s selected by a `DataTemplateSelector` would reintroduce exactly the
divergence this removes — **one template, conditional parts.**

The wedge at 72 rather than 92 is the B5 change: 280 − 72 − 16 − 12 leaves ~180 px of text column against
today's ~164, and three separate clipping incidents are recorded against that budget. The slant is a ratio
of height, so the angle is unchanged.

### 1.3 The page applies `CardMetrics`

```csharp
CardLayout layout = CardMetrics.For(ActualWidth, _consoles.Count);

panel.ItemWidth  = layout.CellWidth;
panel.ItemHeight = layout.CellHeight;
panel.MaximumRowsOrColumns = layout.MaxColumns;

ConsoleGrid.HorizontalAlignment = layout.Density == CardDensity.Hero
    ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
ConsoleGrid.VerticalAlignment = layout.Density == CardDensity.Hero
    ? VerticalAlignment.Center : VerticalAlignment.Stretch;
```

`ShowAddTile` decides whether the ghost placeholder is in the collection. At hero density it is not, and the
`HeroAddLink` becomes a `HyperlinkButton` below the `GridView`, shown on the same condition.

**Focus on load at hero density:** `ConsoleGrid.ContainerFromIndex(0)` → `Focus(FocusState.Programmatic)`,
after `ContainerContentChanging` has settled. Open the window, press A, playing.

### 1.4 Guard it

Add to `PageStyleTests` — it already reads the app's XAML as text from a cross-platform project, which is why
this test can exist at all and why it runs in WSL:

```csharp
[Fact]
public void TheConsoleCardIsDeclaredExactlyOnce()
{
    // The hero and the grid card were separate markup for one release and drifted three ways: the hero
    // could not show the "checking" spinner, its overflow button was under the touch minimum, and changes
    // made to one were routinely not made to the other. One declaration, or the drift comes back.
    string xaml = File.ReadAllText(Path.Combine(RepositoryRoot(),
        "src", "Ripcord.App", "Pages", "ConsolesPage.xaml"));

    Assert.Equal(1, Regex.Matches(xaml, @"<controls:PlayWedge\b").Count);
}
```

---

## 2. The wedge carries the card's state — B1, B2

**Files:** `Controls/PlayWedge.xaml.cs`, `Pages/ConsolesPage.xaml`

Two new dependency properties, both defaulting false, both calling `UpdateRim`:

```csharp
public bool IsHot   { get; set; }   // pointer over the card
public bool IsDown  { get; set; }   // the card is being pressed
```

In `UpdateRim`, the bleed alpha and the rim brush vary:

| State | `BleedAlpha` | Rim |
|---|---|---|
| rest | `0x24` (today) | `Accent` |
| hot | `0x3A` | `Accent`, full strength |
| down | `0x60` | `Accent`, full strength |
| muted | none | `MutedAccent` |

**Every one of these stays suppressed in high contrast** — `AppEffects.AccentWashOpacity(1.0) == 0` already
guards the bleed and must keep guarding it. Hover and press are decorative colour; the shape carries the
meaning, which is the whole argument in `Ripcord.Card.xaml`.

**Focus gets nothing.** The ring owns it. A second focus mark on the wedge rebuilds precisely the confusion
the 2026-09-20 amendment removed.

**The pressed state on the card itself:** a `ScaleTransform` to 0.985 for `RipcordDurationInstant` (83 ms),
gated on `AppMotion.Enabled`, on the `GridViewItem`'s content root. And **leave the wedge hot through
navigation** — do not reset `IsDown` before `PrepareConnectAnimation` runs, so the `ConnectedAnimation` lifts
an element already in its destination state and the handoff reads as one motion.

**Also fix B6/B7 while here:** the hover wash is a `Visibility` toggle with no transition, and it is driven
by raw `PointerEntered`/`PointerExited` rather than the `PointerOver` visual state. Move to
`VisualStateManager` on the container and cross-fade the wash over `RipcordDurationInstant`. The raw-pointer
version does not fire reliably for a pen and can leave a card lit after you navigate away.

---

## 3. The bottom edge — note 3

**File:** `Pages/SessionPage.xaml`

Replace the four independently-margined bottom elements with one structure:

```xml
<Grid VerticalAlignment="Bottom">
    <Grid.RowDefinitions>
        <RowDefinition Height="Auto" />                                  <!-- HUD band -->
        <RowDefinition Height="{StaticResource RipcordTouchShelfHeight}" /> <!-- always reserved -->
    </Grid.RowDefinitions>

    <!-- Row 0: HealthAlert (left), DiagnosticsSummary (stretch), ExitProgressPanel (centre) -->
    <!-- Row 1: TouchControls -->
</Grid>
```

- New token in `Ripcord.Tokens.xaml`: `RipcordTouchShelfHeight` = **90**. It is a reservation, not a control
  size, so it belongs with the touch targets and wants the comment saying why it is reserved unconditionally.
- **Reserved whether or not the bar is showing.** The bar times out after four seconds; a row that collapsed
  would bounce the HUD every time, which is worse than the 90 px it costs on a desktop that never sees it.
- `ExitProgressPanel`'s `Margin="0,0,0,104"` becomes `0` — the row does the work. **Deleting that literal is
  the acceptance test for this change.**
- Rung 1 and rung 2 are now mutually exclusive in the portable layer (landed), so row 0 never holds both.

---

## 4. Connect — C1, C2, C3

**Files:** `Pages/SessionPage.xaml`, `Pages/SessionPage.xaml.cs`, `Sessions/SessionViewModel.cs`

### 4.1 Wire `ConnectGate` (landed, unwired)

`SessionViewModel` holds a `ConnectGate`, offers each `ConnectStage` to it, and composes `StatusHeadline` /
`StatusDetail` from `gate.Shown` rather than from the raw stage. Two things the page must also do:

- **Call `Tick` on the existing stats timer.** The flow reports once and goes quiet, so a stage that hangs is
  promoted by the clock, not by a second report. Without this, a four-second hang never names itself — which
  is backwards, and is what `ConnectGateTests.AStageThatHangsNamesItself` pins.
- **`Reset()` on every new connect and on retry.**

Until the gate is offered a stage the headline holds the console's own name, not `"Starting…"`.

### 4.2 The trail becomes the mark

Replace `TrailOne`/`TrailTwo`/`TrailThree` with the mark: a `FamilyMark` in the console's accent with its
three dashes individually lit, and a white play wedge at its head, lit from the first moment.

Geometry is the 64-unit box from `brand/ripcord-tile.svg`: dashes at `(15,18,9,7)`, `(8,28.5,16,7)`,
`(12,39,12,7)` with `rx=3.5`; wedge `M29 17 L53 32 L29 47 Z`, `stroke-linejoin: round`. Unlit dashes take
`ControlStrongFillColorDisabledBrush`. Draw at 72 px beside the status block.

The reason, since it is the one place this departs from the shipped composition: the dashes are uneven and
right-aligned **because of their relationship to the wedge**. Laid out horizontally with no wedge, both
reasons are gone and the middle segment is 70% wider than the first for a reason nobody can recover.

`Trail.dc.html` in the mock-ups draws all three lit states.

### 4.3 An escape after three seconds

A quiet `Back to consoles` under the status line, shown once any non-terminal connect has run ~3 s. Not on
entry — that would be the ceremony the composition exists to avoid. It reuses `LeaveButton_Click`.

### 4.4 The first frame cross-fades — C4

On the first decoded frame, fade `StatusOverlay` out over 150 ms, identity mark last. Opacity only, which is
exactly and only what `Ripcord.Motion.xaml` permits on the video layer. Gate on `AppMotion.Enabled`.

And **calm the wake wait** — no added personality there. A 10–25 second wait that recurs nightly is the
shape the design already rejects for connect.

---

## 5. Rung 1 renders the new state — D2

**File:** `Pages/SessionPage.xaml`, `Pages/SessionPage.xaml.cs`

- `AlertText` binds `Diagnostics.HealthNotice`, not `Diagnostics.Health`.
- The trailing pill reads `AlertHint`:
  - `Key` → the `F3` pill as today.
  - `None` → no pill.
  - `Tap` → the whole `HealthAlert` border becomes a `Button` that opens rung 2, with a "Tap for detail" pill.
- Call `_viewModel.SetInputMode(...)` from `InputRouter.ModeChanged`. The tracker already debounces; this
  layer takes the answer rather than deciding it.

---

## 6. First run and pairing — E1, E2, E4

**Files:** `Pages/ConsolesPage.xaml`, `Pages/AddConsolePage.xaml` + `.cs`, `Strings/en-US/Resources.resw`

### 6.1 First run (E1)

Replace the empty state. The mark is the **full Ripcord mark** — wedge plus three dashes, in the brand
colours — not `FamilyMark`, which is the console badge and whose own XAML says the wedge is left out because
it is the app's identity. No wordmark; the title bar carries the name.

```
Play your console on this PC.

Ripcord finds your console on your network — keep both on the same
Wi-Fi or wired connection. Add it once and it stays paired.

[ Add your console ]
```

**Amended 2026-09-22: "your console", not "your PlayStation".** This file prescribed the vendor name. The
brand is invoked only where it is factually necessary — signing in to PlayStation Network, or naming a PS5
on a family button — and a headline is not one of those places. It would also date the moment Xbox support
lands, which is the work starting next.

"No consoles yet" goes. It reports a deficiency to someone who has done nothing wrong.

### 6.2 Pairing (E2, as amended in §0)

- Reorder so discovery leads; the family step becomes the manual/not-found path.
- Build the family buttons from implemented platforms. **No dead Xbox button.**
- Delete the `RouteChoice` `RadioButtons` and its two hard-coded `<x:String>` values (E3). The route is
  stated as a fact: signed in and the console is on the account → pair; otherwise the code route leads with
  sign-in beside it phrased as what it saves. The other route stays one quiet link away.
- The step dashes drop from four equal to three uneven — the mark's own, matching the connect trail.

### 6.3 The celebration (E4)

Mark assembling at hero size, **"Paired."** with a period, and **Play now drawn as the wedge** — same
language as every card they will press afterwards, which makes this the app's only tutorial without it
feeling like one. **Done** is a peer, not a save button; the record is already on disk. The name box must not
take focus ahead of **Play now**, or a soft keyboard opens on a handheld at the celebration.

`Celebration.dc.html` and `FirstRun.dc.html` in the mock-ups.

---

## 7. Settings — F1, F2

**File:** `Pages/SettingsPage.xaml`

Collapse **Advanced** into an expander, keep it last. **No search box** — twenty settings on one scrolling
page do not need one, and it is a Windows Settings mannerism rather than a Fluent behaviour, which the
position's own line separates deliberately.

**Amend `docs/design.md`** in the same commit: it currently prescribes the search box.

Also move the live pad readout (`ControllerButtonsText`, `ControllerSticksText`) out of the rung-3 panel and
onto the controls page, beside the bindings it helps check. It is a controller diagnostic, not a stream one —
the one rung move `design.md` specified that has not happened.

---

## 8. Launch at a console — A1

**Files:** `App.xaml.cs`, `Pages/ConsolesPage.xaml.cs`

1. **`--play <name|id>` and `--play-last`.** Parse in `OnLaunched`. Put the parser in `Ripcord.Core` so it is
   testable without a window; it is pure string work.
2. **"Create a shortcut" in the card's overflow flyout.** Writes a `.lnk` with the argument. This is what a
   Steam Big Picture or handheld-launcher user adds as a non-Steam game, and it works in the zip.
3. **Jump list, guarded.** `Windows.UI.StartScreen.JumpList.IsSupported()` is `false` without package
   identity, so the MSIX gets it and the zip silently does not. Rebuild the list when the paired-console list
   changes. No interop; roughly thirty lines.

**Do not add a "connect on open" setting.** Someone who opens the app to rename a console should not find
themselves in a game. Intent belongs to the entry point.

---

## 9. Small ones

| | Where |
|---|---|
| Hero overflow button under the touch minimum (B4) | Fixed for free by §1 — the one template uses `RipcordTouchTarget`. |
| `"Starting…"` vs `"Starting up…"` (C5) | `SessionPage_Starting` in `Resources.resw` and the assessor's Info verdict. Pick one. |
| `design-branch-test-pass.md` §4 contradicts itself | Lists an Accessibility section, then says there is none two lines later. |

---

## 10. What to look at on hardware

Beyond the existing test pass, these are the new things and the order they are worth checking in:

1. **A warm connect shows one calm line, not three.** The thing C1 exists for. Then pull the console's
   network mid-connect and confirm a hang still names its own stage.
2. **Press a card and feel it.** Cold start, first connect of a session, while the D3D12 device comes up.
3. **Rung 2 with the touch bar summoned**, on the handheld *and* on the desktop with a mouse — the collision
   was reported on both.
4. **The one-console layout is still one press.** Open the window, press A. If focus is not on the card at
   hero density, §1.3's focus call is landing before `ContainerContentChanging` settles.
5. **High contrast on the card**, hovering and pressing. Both new states must vanish entirely.
6. **A pad during a lossy stream** — the F3 pill must be gone.
