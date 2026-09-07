# Ripcord app re-imagining — plan

> **Superseded — kept for the record.** This plan describes work that has since landed; it is not a
> current description of the system. See [`../../ROADMAP.md`](../../ROADMAP.md) for what is left,
> [`../journal.md`](../journal.md) for what happened, and [`../architecture.md`](../architecture.md)
> for how the code is arranged now.

> Approved 2026-08-05. Stages: **A** portable extraction · **B** design system + IA · **C** input
> independence. Delivery order is A → B-design-system → C-step-1 → the rest, except that C-step-1
> (the focus search-root fix) is landed first: it is ~15 lines, immediately hand-testable, and it
> validates the "keep `ContentDialog`" decision the rest of Stage B depends on.

## Context

Ripcord's PS4 support closed on 2026-08-05, so the protocol track is effectively done and attention
moves to the app itself. The trigger was two observed UI faults in the new console-add flow, but the
user asked to treat them as symptoms and re-imagine the app flow wholesale rather than patch.

The app is to be judged as a Windows 11 Fluent showcase, for an audience of gamers from novice to
enthusiast, fully operable by **controller, touchscreen, or keyboard+mouse independently**, respectful
of accessibility settings, and eventually ported to macOS/Linux/Apple TV with a native UI per OS.

Decisions taken with the user before planning:

| Question | Decision |
|---|---|
| Scope | **Full re-imagining now** — information architecture, design system, every flow, onboarding |
| Couch use | **One adaptive UI**, always controller-navigable; no separate TV mode |
| Portability | **Extract all app logic out of `Ripcord.App` first**, then redesign on that foundation |
| Streaming surface | **In scope** — HUD, diagnostics and touch bar redesigned too, not just restyled |
| Account ID | **Both paths**: PSN sign-in that fetches it, *and* manual entry — some users will refuse sign-in. Remembered either way |
| Delivery | **Feature branch, one reviewable commit per stage**; `main` stays streamable |

## Confirmed faults (verified in this session, not reported)

**1. Back is unowned, and it silently destroys a completed pairing.**
`MainWindow.xaml.cs:284` does `if (eastPressed && NavFrame.CanGoBack) NavFrame.GoBack();` with no
consultation of the current page, and `TitleBar_BackRequested` does the same. `AddConsolePage` runs a
private 5-step machine (`Family/Find/Link/Pairing/Done`) whose footer Back does *step*-back, and on
`Step.Done` it deliberately disables that button — "the console is paired; going back would mean
re-pairing it" (`AddConsolePage.xaml.cs:145`). But the title-bar arrow and gamepad B remain live, and
`_paired` is only persisted by `Finish()` on explicit Save. **So a successful pairing can be thrown
away by pressing B.** There is no contract by which a page can intercept or veto back intent — this
is the root cause of "clicking back seems to kinda break the page completely", and it is a data-loss
bug, not a cosmetic one.

Related, same page: footer Back from `Link` calls `ShowStep(Find)` then `StartScan()`, discarding
prior results and re-probing the network instead of restoring what was found.

**2. The wizard's width is a function of which step is showing.**
`AddConsolePage.xaml:125` is `<Grid MaxWidth="720" HorizontalAlignment="Left">` holding all five step
panels as overlapping siblings in one cell-less grid, switched by `Visibility`. A collapsed element
measures to zero, so the visible step's natural width alone sets the region's width, re-derived on
every `ShowStep`. `<ProgressBar x:Name="ScanProgress" IsIndeterminate="True"/>` (line 209) is
unwidthed and stretches. Hence "the scanning page abruptly widens/narrows based on if the scanning
progress bar is there." A `MaxWidth` with no floor cannot hold a stable width.

**3. Controller-only users hit dead ends at every modal.**
`AddConsolePage.xaml:1-13` already documents why the pairing flow stopped being a dialog:
*"directional gamepad focus cannot get inside a ContentDialog (it activates, it does not move)."*
Three `ContentDialog`s remain — `DisconnectDialog`, `LoginPinDialog`, `KeyBindingsDialog`. Worse,
mid-session `SessionPage.IsCapturingInput` stays true while a dialog is up (only
`SuspendInputForwarding` is set, `SessionPage.xaml.cs:721`), and `MainWindow.xaml.cs:266` makes the
nav layer ignore the pad entirely in that state — **so a controller-only user on the couch who
reaches the disconnect confirmation cannot drive it at all.** `LoginPinDialog` is worse still: numeric
text entry with no controller path to type into it. This directly violates the couch requirement.

**4. "Larger text and controls" is a broken promise.**
`RipcordSettings.LargeUiScale` has four references repo-wide: the model property, a test fixture, and
the two `SettingsPage.xaml.cs` lines that read and write the toggle. **Nothing consumes it.** The
setting persists and does nothing.

**5. Two controller sources, and the chrome gets the weaker one.**
`ControllerSourceFactory.Create()` — the full `CompositeControllerSource` (DualSense raw HID +
GameInput, OR-merged, hot-swap-safe) — is constructed in exactly one place, `SessionPage.Page_Loaded`
(`SessionPage.xaml.cs:195`), and lives only while that page is loaded. `MainWindow` builds its own
separate **GameInput-only** `_navControllerSource` (`MainWindow.xaml.cs:41`), so chrome pages never
see DualSense PS/touchpad/Create buttons even where the hardware reports them.

**6. No native WinUI gamepad facilities are used at all.**
Repo-wide, zero matches for `XYFocus*`, `IsFocusEngagementEnabled`, `TabFocusNavigation`,
`FocusVisualKind`, `RequiresPointer`. Chrome navigation is a hand-rolled layer over
`FocusManager.TryMoveFocus` plus automation-peer invocation, with manual auto-repeat.

## Baseline worth preserving

The existing code is not sloppy — it is thoughtful code that grew organically, and several decisions
are load-bearing and well-commented. The redesign must not regress them:

- **Accessibility discipline is already good in places**: 129 `ThemeResource` uses across 10 files;
  hardcoded colour confined to a documented set (three brand accents, the About logo art, the video
  letterbox). Status is never colour-only — dot plus text label, with comments saying so.
  `SessionPage` resolves the health dot through `SystemFillColorSuccess/Caution/CriticalBrush` after a
  documented bug where LimeGreen/Orange were wrong in light theme and invisible in high contrast.
- **The diagnostics panel** is the model the user explicitly cited: pinned header over a scrolling
  body (fixing a `StackPanel`-measures-infinite bug), fixed-scale sparklines tied to
  `StreamHealthAssessor` warn thresholds because "an auto-scaled axis makes a calm stream and a broken
  one look identical", pill sets rebuilt only when the set changes, F8 text-report export for
  handhelds with no debugger.
- **The touch bar's** `handledEventsToo: true` wiring exists because `ButtonBase` marks pointer events
  handled before XAML handlers run, which silently broke hold-to-open-power-menu. Its text labels
  (not glyphs) are a deliberate choice to avoid reproducing Sony's registered button iconography — a
  constraint any new affordance system inherits.
- **`SessionController`** (`src\Ripcord.Client`) is already the portability exemplar: plain net10.0,
  all dependencies injected, unit-tested against fakes, with an explicit "caller marshals to its own
  dispatcher" threading contract.
- **`ICredentialProtector`** is the exemplar for Windows-only implementations behind a portable seam.
- `GridView`/`ItemsWrapGrid` on `ConsolesPage` is used specifically for its real built-in directional
  focus behaviour — native where native works.

## Known debt to fold in

- `AboutPage` and `SettingsPage` still carry local duplicate `Card`/`Chip`/`SectionHeader` styles;
  `Styles\Ripcord.xaml` was created to end exactly that duplication and the migration never finished.
- No DI anywhere; every page `new`s its own `SettingsStore` / `PairedConsoleStore`. Page-to-window
  calls go through `App.MainWindow` cast to the concrete `MainWindow` type.
- `Ripcord.App` has **zero test coverage** and cannot be referenced by the one test project
  (net10.0-windows). The pairing flow is untestable as written.
- No `AccessKey` mnemonics and no explicit `TabIndex` anywhere; tab order is declaration order.

## Open question to settle during implementation, not invent

Where a PSN account ID legitimately comes from on the manual path — what a user can read off their
own console or account without sign-in, and what format validation is honest. ROADMAP lists live
OAuth login as blocked on a client-credential policy decision, so the **manual path must stand alone**
and sign-in is additive. This is a protocol/product question; it gets marked and answered, not guessed.

---

# Stage A — extract the app layer out of `Ripcord.App`

Nothing in `Ripcord.App` is linkable by another front end (it targets `net10.0-windows`), so "extract
first" means giving the app layer a real home before redesigning it.

## Assembly layout

Two new plain-`net10.0` projects, plus a move into `Ripcord.Core`:

```
src/Ripcord.Presentation/            → Ripcord.Core, Ripcord.Client, Ripcord.Diagnostics
src/Ripcord.Presentation.Halyard/    → Ripcord.Presentation, Ripcord.Protocol.Halyard(.Common)
src/Ripcord.Core/Consoles/           ← PairedConsole(+Store, +CredentialStore) moves here
tests/Ripcord.Presentation.Tests/    → Ripcord.Presentation ONLY (no protocol, no UI)
```

**Why not just widen `Ripcord.Client`:** its csproj charter is "no UI or render dependencies" and it
references only Core + Diagnostics. Everything extracted needs something it deliberately lacks. It
also referencing Halyard would erode `ConsoleFamily`'s job as the boundary between vendor-codename and
product-name vocabularies. `Presentation → Client` is a downward edge and the right shape.

**Why the Halyard split:** it applies CLAUDE.md's own crypto-seam pattern. The payoff is concrete —
`Ripcord.Presentation.Tests` references one project, so the pairing state machine is tested with no
UDP, no ports 9302/987, no cipher, no fixtures.

**Why persistence goes to Core, not Presentation:** `PairedConsoleStore` is the structural twin of
`Ripcord.Core/Settings/SettingsStore.cs` — `IPlatformPaths` + source-gen JSON + temp-file-and-move +
graceful-degradation catch. `IConsoleCredentialStore` is already in Core. And `tools/Ripcord.ProtocolLab`
(net10.0, no UI) should be able to read the console list without referencing a presentation assembly.
Its one inverted dependency, `PairedConsole.ToRecord(...) → HalyardPairingRecord`, becomes an extension
method on the Halyard side (one caller: `SessionPage.EnsureConsoleAwakeAsync`).

## Presentation without a UI framework: semantic tokens

`Brush`/`Color`/`Visibility` must not appear in the portable layer, but the logic choosing them must be
shared. Three portable enums plus `bool`, with a new `src/Ripcord.App/Converters/` folder mapping them
to WinUI types:

- `AccentRole { PlayStation, Xbox, Nintendo }` — colour values stay in `Styles/Ripcord.xaml`, so the
  palette keeps one home.
- `StatusTone { Unknown, Positive, Caution, Neutral }` — the *meaning*, never the colour. Offline is
  `Neutral`, not `Critical`: a powered-off console is normal, and colouring it as a fault cries wolf.
- `ActionGlyph { Play, Wake }`.
- `bool IsChecking` replaces the `CheckingVisibility`/`SettledVisibility` pair — the inverse is a
  converter's job, not a second property.
- The opacity magic numbers (wash 0.22/0.10, action 1.0/0.5) leave the view-model entirely.
  `IsHighlighted`/`CanConnect` are the portable truth; how strongly a hover reads is a styling choice.

## One immutable state record, one notification — reject MVVM frameworks

`ConsoleListItem`'s `Status` setter hand-raises **11 property names** (verified, lines 115–125), and its
own comment warns: *"Missing one of these leaves a card reading 'Offline' above a 'Connect' button."*
A second cascade of 7 sits at lines 258–264.

That is not an argument for `CommunityToolkit.Mvvm` source generators — it is an argument for **not
having per-property notification at all**. Each view-model becomes an immutable state record recomposed
whole by a `Compose()` override, behind a ~40-line `ObservableState<TState>` base exposing exactly one
notifying property plus an `IObservable<TState>`. A record cannot be half-updated, so the failure mode
is gone by construction rather than by discipline, and record value equality answers "did anything
change?" once, centrally, instead of via an early-return guard in every setter.

Two notification channels over the same immutable value, so neither front-end shape is second-class:
`INotifyPropertyChanged` on the single `State` property (what `x:Bind` and Avalonia want) and
`IObservable<TState> Changes` (what SwiftUI/Combine, a GTK signal, or a NativeAOT C-ABI callback want —
and the shape `SessionController.Status` already publishes, so it is the house pattern).

Cost accepted: a status change re-evaluates the name binding too. For ~8 cards that is the right trade
for deleting the cascade.

**One deliberate exception:** collections stay `ObservableCollection<T>` (a `System.ObjectModel` type,
not a UI type). `INotifyCollectionChanged` is the only thing giving *in-place* list updates; a snapshot
list would rebuild the `GridView` on every probe resolution and **destroy focus** — and focus is
load-bearing here, because the grid is driven by a gamepad.

## The pairing flow becomes a testable state machine

`AddConsoleFlow` in `Ripcord.Presentation/Pairing/`, behind two seams — `IConsoleScanner` (an
`IObservable<DiscoveredConsole>`) and `IConsoleRegistrar` (`CheckAvailability` + `RegisterAsync`) —
implemented in `Ripcord.Presentation.Halyard`. The multi-profile `Task.WhenAll` fan-out is protocol
plumbing, not flow logic, so it moves down into `Ripcord.Protocol.Halyard` as a
`HalyardAllFamiliesDiscoveryService` the existing test project can already exercise.

This is also where the three known defects get fixed structurally:

- **`async void StartScan` → `Task`-returning**, so tests await and assert instead of `Task.Delay`-and-hope.
- **Cancellation is owned by the flow**, and `DisposeAsync()` cancels an in-flight *pairing* too — which
  today it does not: `PairAsync`'s `using var cts` is only a timeout, so leaving mid-pairing leaves the
  request running and a result landing on a dead page.
- **Post-navigation mutation** is fixed by a generation check (`ReferenceEquals(_scanCts, cts)`) inside
  the single place results are accepted, rather than at each callback. The existing `finally` block
  already uses exactly this idiom — this applies it to the result path, which is the half that was missing.
- `CheckAvailability` runs **before** entering `Pairing`, so an unavailable cipher reports on the Link
  step instead of flashing a pairing panel the user cannot act on.

> **Carry `ClientDeviceId` over byte-for-byte and raise it as a question, do not "fix" it.**
> `AddConsolePage.xaml.cs:495` passes `RandomNumberGenerator.GetBytes(32)`, while
> `HalyardRegistrationRequest`'s own doc says "16-byte device id (RP-Did material)"; separately
> `HalyardDeviceIdentity` calls RP-Did "a stable 16-byte machine id" and `HalyardSessCtrlFields` documents
> the RP-Did *wire plaintext* as a fixed 32-byte structure containing it. Pairing works, so the console
> evidently does not cross-check the registration device id against the session's RP-Did — but that is an
> untested assumption. Changing it during an extraction would land as a refactor and behave as a protocol
> change. Mark it `[X]` in `ROADMAP.md` and derive it separately.

## Threading — one seam, stated once

`IUiDispatcher { bool IsOnUiThread; void Post(Action); }`. The rule, stated in one place: view-models
never marshal at their call sites; they mutate through `ObservableState.Mutate`, which is the only place
a post happens. Consequently every view-model field is read and written on the dispatcher thread alone
and **nothing in this layer takes a lock** — a deliberate difference from `SessionController`, which
genuinely is multi-threaded and pays for it with `_gate`/`Volatile.Read` throughout.

This discharges `SessionController`'s existing contract ("observers are notified on arbitrary threads;
a UI consumer must marshal") once, on behalf of the whole layer.

The `IsOnUiThread` fast path is correctness, not optimisation: a handler that calls `SetLinkInput(...)`
then reads `State.CanPair` on the next line must not see the pre-change value. Implementations:
`DispatcherQueueUiDispatcher` (WinUI), `ImmediateUiDispatcher` (tests — runs inline, so every assertion
is synchronous), `SynchronizationContextUiDispatcher` (a future .NET front end).

## DI — hand-rolled composition root, no container

The graph is ~8 singletons and ~5 factories, eagerly built at startup, no scopes, no lifetimes, no
scanning. `Microsoft.Extensions.DependencyInjection` buys a reflection-activation container and — the
real cost — an `IServiceProvider` in reach, which becomes service location at the first awkward call
site. Two project-specific reasons against: the csproj already documents `PublishTrimmed` being off with
source-gen JSON as the intended fix, so reflection activation works against a committed AOT/trimming
direction; and a NativeAOT shared library for a future Swift front end is exactly where a reflection
container is the first thing you must remove. A hand-rolled root is also this codebase's demonstrated
aesthetic — every existing seam is a constructor parameter with a documented default.

`RipcordAppServices` (portable) + `HalyardAppServices.Create(...)` (the one place a front end names
Halyard), built in `App.OnLaunched` before the window. Pages resolve their view-model in the
constructor **before `InitializeComponent()`**, so compiled bindings see a live object on the first
pass — this keeps `Frame.Navigate(typeof(T))` untouched, no navigation service, no page factory.
(Rejected: passing the view-model as a navigation parameter — `OnNavigatedTo` runs *after*
`InitializeComponent`, so every `x:Bind` evaluates against `null` first and each page needs
`Bindings.Update()`, which is a per-page thing to forget.)

**`IShellNavigator`** replaces the three `App.MainWindow as MainWindow` casts;
`MainWindow : Window, IShellNavigator`. This is also where **page-owned back intent** lands, fixing
fault 1 — see the Input stage.

## Tests

`Ripcord.Presentation.Tests` with fakes in `SessionControllerTests`' existing style (scripted scanner
with per-family faults and gates; scripted registrar including never-completes; `InMemoryPairedConsoleStore`;
fixed clock; recording `delay`). Highest-value cases, all currently impossible:

- Pairing flow (~30 cases): result arriving after dispose is dropped; result from a superseded scan is
  dropped; cipher-unavailable never enters `Pairing`; registration budget exhausted reports on Link;
  back during pairing refused; host-id preferred over address as identity, address for a typed host;
  credential stored encrypted not plaintext; nickname only pinned when it differs; one family faulting
  keeps the others' results; a PS4 found during a PS5 pick adopts its own family.
- Reachability (~18): `200`→Online, `620`→Resting, no-reply→Offline; each row stays Checking until its
  own probe resolves; one slow console does not delay others; rest-watch settles early on first Resting
  reply, survives a transient no-reply, and reports the last observed state when its 6×10 s budget runs
  out; rest intent consumed once.
- `LastPlayed.Describe` boundaries with a fixed clock (119 s, 2 min, 59 min, 60 min, 24 h, 7 d, null).
- **`Presentation_ReferencesNoUiFrameworkAssembly`** — reflect `GetReferencedAssemblies()` and assert
  nothing matches `Microsoft.UI*`/`Microsoft.Windows*`/`WinRT*`/`Avalonia*`/`Gtk*`. Twelve lines, and it
  is what keeps Stage A's outcome true through the Stage B redesign; without it the first stray
  `using Microsoft.UI.Xaml;` silently undoes the whole thing.

## Stage A order — app builds and streams after every step

0. Scaffold the projects + `Ripcord.slnx` entries (nothing references them yet).
1. **Persistence → `Ripcord.Core/Consoles/`** (`internal`→`public`, extract `IPairedConsoleStore`, move
   `ToRecord` to a Halyard extension). On-disk format unchanged. *Touches the credential path — verify
   existing pairings still load and stream before going further.*
2. **`AppRegistrationCipher` → `HalyardRegistrationCipherResolver`**, moved verbatim (its resolution
   order and `source` strings are user-facing error text). *Verify: pair from scratch.*
3. **`ConsoleFamily` split** — portable record + `AccentRole`; add `Converters/`. *Verify: picker and
   card marks pixel-identical.*
4. `IUiDispatcher` + `ObservableState<T>` + the portability test. No behaviour change.
5. **`ConsoleListItem` → `ConsoleCardViewModel` + `ConsoleCardState`.** *Highest visual-regression risk
   in the stage — compare against screenshots in both themes, including hover and focus.*
6. **`ConsolesViewModel` + reachability monitor** behind `IConsoleReachabilityProbe`. *Verify: rest-on-
   disconnect shows "Going to sleep…" → "Rest mode".*
7. **`AddConsoleFlow`** — `AddConsolePage.xaml.cs` drops ~577 → ~180 lines. *Verify: pair for real, then
   repeat while navigating away mid-scan and mid-pairing.*
8. **Composition root** — delete 3 `new SettingsStore()`, 3 `new PairedConsoleStore()`, 3 window casts.
   Self-verifying: a missing dependency now fails at startup with a stack trace.
9. **`SessionViewModel`** behind `IVideoPipelineStats`/`IConsoleWakeCoordinator`.
10. **`SettingsViewModel`** — the `_loading` bool disappears, since its only reason to exist is that XAML
    parsing raises `ValueChanged` before later controls exist, which is not a problem a view-model has.
11. Cleanup + CLAUDE.md/ROADMAP updates.

**The line for step 9, drawn explicitly:** anything producing a string, bool or enum goes portable;
anything touching a device, presenter or native handle stays. D3D12, the input stack,
`SetThreadExecutionState`, `ConnectedAnimation` and the fullscreen presenter are not extractable.

**Not incremental — flagged:** step 5 is atomic per binding surface (`x:Bind` in a `DataTemplate`
compiles against exactly one `x:DataType`, so the card template moves wholly or not at all). The
tempting bridge — a WinUI adapter exposing `Brush`/`Visibility` over the portable view-model — must be
**rejected**: it is precisely the layer this stage exists to delete, and it would survive into Stage B as
the thing everyone binds to. Steps 1, 2 and 7 all touch credential/registration paths and need a real
console; batch them into one hardware session in that order.

---

# Stage B — information architecture, design system, motion, accessibility

## The organising idea, already in the codebase

The diagnostics panel is the app's thesis and nobody wrote it down. It works because it is a
**three-rung disclosure ladder**, each rung entered deliberately:

| Rung | Content | Where |
|---|---|---|
| 1 — the verb | The state in one word, the action in one press | Console card, stage status |
| 2 — one gesture away | Why, and what you'd change | Card details, health verdict + tip |
| 3 — opt-in, remembered | Instruments: sparklines, decode path, constants source | Diagnostics overlay, F8 report |

Two invariants follow, and they settle most arguments by themselves: **no rung-3 fact appears at rung 1**,
and **every rung-3 surface must be usable without a mouse and savable to a file** (F8 already does this).

## IA: retire `NavigationView` — the library is the shell

`NavigationView` disambiguates five or more peer destinations. Ripcord has **one destination and two
utilities**. "Consoles" as a pane item names the app's only home, so the pane's steady state is a column
whose sole function is to highlight the page you are already on — and on an Ally X at 1280 px that is most
of a fourth card. It is also *why* `StreamFrame` had to be invented: the content area's border and corner
radius (`MainWindow.xaml:69-80`). And a gamer's first press should be Play, but the first focus stop today
is the nav pane (`FocusFirstNavItem`, `MainWindow.xaml.cs:387`).

Replace with a **two-layer window**: a chrome layer (`TitleBar` + one `Frame`) and a stage layer. The
stage stays separate, now for a principled reason rather than a workaround — it owns the whole window,
never enters a back stack, and is immune to `GoBack`. Settings and About move to `TitleBar.RightHeader`
commands (gear + overflow), each one focus stop with an `AccessKey`. Delete the pane-toggle handler.
`MicaBackdrop` is already set correctly (`MainWindow.xaml:11-13`).

Page set: `WelcomePage` (first run), `HomePage` (renamed `ConsolesPage`, the only page cached — it keeps
grid scroll position and the realized containers `PrepareConnectAnimation` needs), `PairPage` (renamed
`AddConsolePage`, **never** cached — a flow must start clean), `SettingsPage`, `ControlsPage` (absorbs
`KeyBindingsDialog`), `AboutPage`, `SessionPage` (the stage, in no nav stack).

**`HomePage` adapts to console count**, because one or two is the real case:
- **0** → never the empty grid; go straight to `WelcomePage`.
- **1** → **hero layout**: one wide card, large family mark, status, and a real `AccentButtonStyle`
  **Play** button holding focus on load. Open window → press A/Enter → playing. That is the product.
  ("Add another console" sits below as a quiet link.) This is the one-press-play decision.
- **2–6** → today's card grid, ghost tile after the last card.
- **7+** → grid plus a `SelectorBar` filter (All/Ready/Resting). Defer, but structure the header for it.

Breakpoints on the standard Windows ones, in **effective** pixels: <640 one column; 640–1007 two;
≥1008 three-plus, page content centred at 1400 so an ultrawide doesn't stretch a card row absurdly.

## The pairing flow: one stationary card, one back model

Still a page (the gamepad/`ContentDialog` constraint), but **one stationary flow card with content swapped
inside it** — because both reported bugs are the same root cause: *layout derived from the current step's
content*.

**Fault 2 (width jitter) — two structural fixes, both required.** Width comes from the parent, never from
content: `HorizontalAlignment="Stretch"` + `MaxWidth` instead of today's content-sized `Left` + `MaxWidth`.
And **one step in the visual tree at a time** via a `ContentControl` over a step view-model with
`ContentThemeTransition`, deleting the five-overlapping-panel pattern and its five `EntranceThemeTransition`
copies. Plus a generalisable rule: **a transient element never changes layout — reserve its space.** The
scan progress becomes a permanent 4 px rail whose `IsIndeterminate`/`Opacity` change, never its presence.

**Fault 1 (two backs) — one back gesture for the whole app.** Delete the flow's footer Back button
entirely; the title-bar chevron is back's only visible form. Add a shell contract:

```csharp
internal interface IShellBackHandler
{
    bool CanGoBack { get; }
    bool TryGoBack();                     // true = handled here; false = let the frame go back
    event EventHandler? CanGoBackChanged;
}
```

Every route — chevron, `Escape`, gamepad East — funnels into one `MainWindow.RequestBack()`: dialog open →
WinUI handles it; current page is an `IShellBackHandler` and handles it → done; else `ChromeFrame.GoBack()`.
B, Esc and the chevron become the same thing *by construction*, which is the actual fix. No amount of
patching two affordances makes them agree.

**The Done-step data loss — remove the state rather than guard it.** Today the record only reaches disk in
`Finish()`, so *any* exit from `Done` loses a successful pairing, and disabling the in-page Back cannot stop
the title bar or B. Fix: **`Upsert` in the success branch of `PairAsync`, the moment registration returns
good.** The Done step then edits an already-saved console — naming is a second `Upsert`, Play starts the
stage, and leaving by any route (chevron, B, Esc, Alt+F4) keeps the pairing. The unsaved-success state stops
existing, so the bug has nowhere to live. Two deliberate consequences: back from `Done` means "I'm done"
and returns to `HomePage` with the new card arriving via `ConnectedAnimation`; and back during `Pairing` —
the one genuinely non-interruptible step, where the console has consumed a code — is the **only** place back
may ask a question (a two-button dialog, legal under the rule below).

**Back from Link stops destroying the scan.** With the flow view-model owning the discovered collection
across steps, back to Find restores results; only "Search again" rescans. Free, once state leaves the page.

**Dialog rule, which fixes a live bug:** `ContentDialog` is permitted **only** with at most two buttons and
no input fields. `LoginPinDialog` has a `TextBox` and appears *mid-stream*, so a controller-only user
currently cannot enter their console login passcode at all — it becomes a stage panel. `KeyBindingsDialog`
becomes `ControlsPage`. "Remove this console?" may stay a dialog.

## The account ID

Verified encoding: `HalyardRegistrationMessage.EncodeAccountId` does `ulong.TryParse` → base64 of a
**little-endian uint64** ("wire-confirmed"), with a fallback that base64s UTF-8 bytes when parsing fails.
That fallback matters for UX: **a typo'd ID is not rejected, it silently takes a different encoding path.**

Design, honouring the user's "both paths" decision:
1. **Ask once per account, never per console.** Stored app-level and DPAPI-protected through the existing
   `ICredentialProtector` — it is a personal identifier, so it gets credential treatment and the same
   never-log rule as `CredentialBlob`. Console #2 never asks.
2. **Two ways to supply it, both first-class.** PSN sign-in fetches it (`HalyardAccountInfo.AccountId`);
   manual entry stands alone and is never presented as the lesser path, because some users will refuse
   sign-in. Since ROADMAP lists live OAuth as blocked on the client-credential decision, **manual is what
   ships first** and sign-in is additive.
3. **Off the pairing critical path** — its own step, shown only when nothing is stored (first run, or step 0
   of the flow). The Link step then asks for the 8-digit code and nothing else, which is what the console
   screen is actually prompting for.
4. **Validate as a value, not as a non-empty string.** Enable Pair when it parses as a 64-bit integer;
   fail with a specific message rather than a permanently dead button. Accept pasted whitespace.
5. **Explain it where it's asked**, in an `Expander`, so the paragraph isn't tax on every reader.

> **Open protocol question — file it, don't guess it.** Does the console *validate* the account id at
> registration, or merely record it? If it validates, the field is unavoidable until sign-in ships. If it
> merely records it, the field can disappear entirely and become a per-install value — a whole step deleted.
> The experiment is cheap (pair with a well-formed but wrong id; see whether registration succeeds *and* the
> stream comes up). Belongs in `ROADMAP.md` beside the OAuth item.

## Design system

Split `Styles/Ripcord.xaml` into `Ripcord.Tokens/Text/Surfaces/Motion.xaml` merged from one entry point
(`App.xaml` keeps its single line, and the after-`XamlControlsResources` ordering constraint carries over).

- **Spacing:** a 4 px scale as `x:Double` tokens plus `Thickness` tokens, since XAML can't do arithmetic.
  **Rule: vertical rhythm comes from a container's `Spacing`, never a `Margin` on a text style.** This is
  what lets the duplicate section-header styles actually merge — today the copies differ *only* in margin
  (`0,16,0,0` vs `0,16,0,4`), a difference nobody chose.
- **Type:** never a literal `FontSize` outside a `FontIcon`. Each role maps to a Ripcord style `BasedOn` a
  stock style, overriding only `FontSize` with a **token** — that indirection is what makes scaling possible.
  Icon sizes get four tokens, collapsing the scattered 12/14/16/18/22/24 literals.
- **Radius:** keep the five existing values, add semantics, and two rules — never override
  `ControlCornerRadius`/`OverlayCornerRadius` (stock controls keep the system's), and a nested surface's
  radius must not exceed its parent's minus the padding between them.
- **Elevation, four levels, one shadow:** L0 Mica backdrop (nothing opaque painted over it) · L1 grouped
  content · L2 interactive card · L3 transient-over-content (the *only* place `ThemeShadow` appears).
  **L2 never gets a drop shadow** — Windows 11 separates cards with stroke and fill, and a shadowed card is
  the loudest "this app came from elsewhere" tell. And **a surface exists only when its content is
  independently actionable**; grouping is a header plus spacing, not a box.
- **Materials:** in-app acrylic only at L3 and only over content genuinely behind it — over video correct,
  **over Mica wrong** (double-blur goes muddy). **Suspend the backdrop while streaming** (`SystemBackdrop =
  null` on show, restore on close): the window is fully covered by opaque video, so Mica is otherwise
  blurring the wallpaper every frame for nothing — invisible, measurable, a battery win on a handheld.

**Two unread accessibility settings, the one real gap in an otherwise strong story.** Nothing in the tree
reads `UISettings.AdvancedEffectsEnabled` or `AccessibilitySettings.HighContrast`. When transparency is off,
every L3 acrylic must fall back to a solid brush. In high contrast the vendor accent wash must go to
**zero** — HC does not permit decorative colour outside the system palette — while `FamilyMark` resolves to
a system brush.

**Finish killing the duplicate styles.** Verified counts: `SessionPage` 7 local styles (including a
`CapabilityPill*` family duplicating the shared `RipcordPill*`), `AboutPage` 3, the others 1 each. Make the
shared set a *superset* first so migration is deletion rather than negotiation; strip `Margin` from the
section-header style per the rhythm rule; then delete per page, one commit each. **Then make regression
impossible**: a test that scans `src/Ripcord.App/**/*.xaml` and fails on any `<Style>` in `Page.Resources`
that isn't an items-control container style. `BundledInteropConstantsTests.Bundle_CarriesNoLiveVectorMaterial`
is the cultural precedent — prose alone has already failed here once.

**Adopt `CommunityToolkit.WinUI.Controls.SettingsControls`** (`SettingsCard`/`SettingsExpander`) — the
highest-leverage native-feel move available: `SettingsPage.xaml` is 463 lines of hand-rolled two-column
grid-in-border, and the Toolkit version is shorter and gets keyboard/screen-reader behaviour free. Two
flags: it is the first non-SDK `PackageReference`, and ROADMAP Track B wants `PublishTrimmed=true` — so
**measure it against trim analysis before committing.**

## Motion

**One gate, and no `if` at call sites.** Grow `AppMotion` into the motion authority by making durations
collapse to `TimeSpan.Zero` when motion is off — the gate becomes the API, so no future animation needs its
own remembered check. Add a sibling `AppEffects` for the three unread preferences with one `Changed` event.
Prefer WinUI's own shipped durations/easings (`ControlFasterAnimationDuration`,
`ControlFastOutSlowInKeySpline`); don't invent a curve Windows already ships — that is the whole difference
between matching the OS and approximating it.

**What animates — a closed list of five:** page transitions (`DrillInNavigationTransitionInfo`);
the card↔stage `ConnectedAnimation`, **extended to the return trip** (today leaving a stream is a hard cut);
items arriving over time (`AddDelete` on discovery, add `Reposition` to the grid so a rename settles rather
than jumps); the flow's single content swap; and exactly two celebrations — the pair check-mark drawing on,
and the first video frame fading up from black.

**What never animates:**
- **Numbers.** The headroom bar already learned this: tweening made a 22→10 Mbps drop read as a slide.
  Generalised — *a diagnostic never animates, because motion implies interpolation and a measurement must
  not appear to interpolate.*
- **Anything on the path to play.** Every animation is fire-and-forget; the mechanism completes whether or
  not it ran.
- **Focus.** At the 120 ms gamepad repeat interval, a follow-the-focus animation is permanently behind the user.
- **Anything on the video layer** but opacity, under 150 ms — transforms and blurs cost the decoder frames
  on exactly the hardware where frames are scarcest.
- **No ambient motion and no launch cascade.** A stagger is the generic-app tell and it costs first-input
  latency at the moment the user is pressing Play.

Enforce structurally: a test asserting `Storyboard`, `ConnectedAnimationService`, `.Begin(` and
`StartAnimation(` appear only in `AppMotion.cs`.

## Making `LargeUiScale` real

**The bigger bug is that Ripcord ignores Windows' own text scale entirely.** `UISettings.TextScaleFactor`
(100–225%) is not applied automatically to WinUI 3 desktop content the way it was in UWP, and nothing here
reads it — so a user at 150% gets 100% Ripcord. *Verify this empirically at 150% before building the fix,
then fix it first.* The inert toggle currently disguises the gap by looking like the answer to it.

The app-level control still earns its place: the OS setting is global (wanting Ripcord couch-readable isn't
wanting Word bigger) and scales **text only**, which is the wrong axis when hit targets and card sizes
matter. So `effective = clamp(OS factor × (LargeUiScale ? 1.3 : 1.0), 1.0, 2.25)` — it *multiplies* the OS
factor, and there is no value below 1.0, because an app-level "compact" that undoes an accessibility choice
is not on offer. Copy becomes honest: *"makes Ripcord's own text and buttons bigger, on top of your Windows
text size."*

**Mechanism: compute the scale in C# and write tokens into `Application.Current.Resources`**, with every
size reached through those tokens (`ThemeResource`, not `StaticResource`, so runtime changes take). Also set
WinUI's own `ContentControlThemeFontSize`/`TextControlThemeMinHeight` so text inside stock control templates
scales too. Round to whole pixels to avoid half-pixel strokes.

Rejected, with reasons: **`ScaleTransform` on the shell root is fatal** — `ContentDialog`, `MenuFlyout` and
tooltips render in the popup root *outside* the transform, so every dialog would stay small; it also blurs
1 px strokes and would scale the `SwapChainPanel`. Redeclaring the stock ramp misses control-internal text.
Two hand-written dictionaries can't express a continuous OS factor of 137%.

Doesn't scale: the `SwapChainPanel` and video (pixels, and it's the console's resolution). Easy to forget
and must scale: **the diagnostics overlay** — precisely the thing being read at seven inches.

## Tone

House voice: **a competent friend with a stopwatch.** Warm in the seams, silent in the middle, never funny
about a failure. Rules, with real before/after:

1. **Say what happens next, not what the system did.** `"Registration crypto unavailable: {source}"` →
   `"Pairing needs Ripcord's protocol data, and this build couldn't load it. (Source: {source})"` — the
   technical fact survives, demoted to where the audience that wants it will find it.
2. **Name the verb the player came for.** `"Connect"`/`"Wake & connect"`/`"Not reachable"` →
   `"Play"`/`"Wake & play"`/`"Can't reach it"`. "Connect" is our word for a mechanism; keep it in
   diagnostics, drop it from the card.
3. **Shorter and active, same facts.** `"Pick one to start streaming. A console in rest mode will be woken
   for you."` → `"Pick a console to play. If it's resting, Ripcord wakes it."`
4. **Verdict first, numbers second** — `StreamHealthAssessor` is already the standard. Apply it to connect
   failures, which today pass a raw controller detail string as the human-facing line.
5. **Personality gets exactly three rooms and never an error path**: the empty/first-run state, the
   successful pair, and the wake wait. `"The console was in standby."` → `"Waking your PS5 — usually about
   ten seconds."` The delight is accuracy, not whimsy. `"Paired."` with a period, never "Success!". Never
   randomize — a rotating quip is a joke told twice.
6. **Never apologise for hardware, never blame the user.** An offline console is a normal state, and the
   code already knows this (grey, not red); the copy should match.
7. Sentence case; `…` not three dots; no "please"; units in labels not values (learned from `"6017.8 Mbps"`).

**Out of the way, always:** over video, every failure path, the diagnostics numbers (read as instruments — a
joke there costs the trust the whole panel earned), and anything appearing more than once per session.

---

# Stage C — input independence

## The headline: a documented project conclusion is wrong, and it is a one-line bug

`AddConsolePage.xaml:8-9` records, as settled fact, that *"directional gamepad focus cannot get inside a
ContentDialog (it activates, it does not move), so the flow could not be driven with a controller at all."*
That symptom is real. The attribution is not. **Verified root cause:**

- `MainWindow.xaml.cs:375` — `new FindNextElementOptions { SearchRoot = Content }`. A `ContentDialog`,
  `MenuFlyout` and every `ComboBox` dropdown render in the **XamlRoot's popup root, which is not a
  descendant of `Window.Content`** — so `TryMoveFocus` cannot see them. Focus cannot *move* into a popup.
- `MainWindow.xaml.cs:409` — `FocusManager.GetFocusedElement(xamlRoot)` is **XamlRoot-wide**, so activation
  *can* see an element inside a popup. Focus *activates* fine.

That asymmetry produces precisely the recorded words. The project observed correctly and blamed the wrong
component. Fixing the search root to prefer the topmost open popup
(`VisualTreeHelper.GetOpenPopupsForXamlRoot`) makes all three dialogs, the console overflow flyout, and all
six `SettingsPage` combo dropdowns directionally navigable **with no markup change**.

Consequence for Stage B: **keep `ContentDialog`.** Rebuilding every modal as a bespoke focus-trapping overlay
would discard light-dismiss, scrim, Escape/Enter and correct UIA modality to work around a bug that a small
fix removes. `AddConsolePage`'s promotion to a Page was still right — but for its *other* stated reason (a
six-field form isn't a dialog), not this one. The Stage B dialog rule stands on its own merits: dialogs stay
for ≤2 buttons and no input fields, `LoginPinDialog` still moves to a stage panel because a text prompt over
live video is wrong regardless, and `KeyBindingsDialog` still becomes a page because rebind-by-capture
fights a host that reserves Enter and Escape.

## Four more verified defects in the same layer

1. **`SettingsPage` is largely pad-unusable.** `ActivateFocusedElement` (`MainWindow.xaml.cs:417-428`)
   handles only `Invoke`, `SelectionItem` and `Toggle`. `ComboBox` exposes `ExpandCollapse`; `Slider`
   exposes `RangeValue`. Verified counts on that page: **6 ComboBoxes, 2 Sliders, 9 ToggleSwitches** — so a
   controller can work the nine toggles and nothing else.
2. **Seeded focus is invisible focus.** `FocusFirstNavItem` focuses with `FocusState.Programmatic`
   (`MainWindow.xaml.cs:391, 397`), which does not draw a focus visual. All programmatic focus must use
   `FocusState.Keyboard`.
3. **The gamepad remap is session-only.** `InputBindings.GamepadRemap` is applied inside
   `MergedInputSource.OnPadFrame`, so a pad that reports South as East navigates the chrome with A/B swapped
   while its game input is correct.
4. **An affordance that doesn't exist.** `ConsolesPage.xaml:24` claims the card overflow is reachable by
   "the pad's menu button". Nothing maps a pad button to `ContextRequested` — only South and East are handled.

## Architecture: one router, a scope stack

One app-scoped `InputRouter` owns a single `ControllerSourceFactory.Create()` composite, applies the remap
**once**, tracks the active input mode, and arbitrates through a stack of `InputScope`s
(`Chrome`/`Session`/`Modal`). `MainWindow`'s duplicate GameInput-only `_navControllerSource` and
`SessionPage.IsCapturingInput` both die.

`InputScope` carries everything the arbiter needs in one object — focus root, initial focus, prompts, and
what Back/Menu do — so "who owns the pad", "where can focus go", "what does B do" and "what does the hint bar
say" cannot drift apart. `InitialFocus` is a `required` member: **you cannot construct a scope without saying
what gets focus.**

Why this beats today's mechanism: `IsCapturingInput` is a *poll* of a page-owned bool derived from a third
object's status, it forces `MainWindow` to know the page's type, and **it is true during modals — which is
the couch dead end.** Scope push/pop is an *edge*, owned by whoever changed state, and "Modal covers Session"
becomes structural rather than two objects agreeing.

The mid-session fix falls out: pushing a `Modal` scope deactivates the `Session` scope, which sets
`SuspendInputForwarding` (already emitting one neutral frame, releasing the held exit combo), stops feeding
the exit detector, and roots `FocusPilot` at the dialog. The whole `try/finally` in `LeaveSession` disappears.
Also extend the keyboard guard — `TypingSomewhere() || Top?.Kind == Modal` — so keys during a modal never
leak to the console either.

**Cost to state honestly in the router's doc comment:** the DualSense HID source now polls for its device for
the app's whole life rather than only during a session — one idle thread at 2 Hz, which is what buys
DualSense PS/Create/touchpad buttons on chrome pages. If it ever shows on a handheld battery trace, the lever
is `Stop()`/`Start()` on `Window.Activated`, not reverting to two sources.

**Keep the deferred construction.** `InputRouter.Start()` must stay behind window realization — building a
GameInput-backed WinRT component before the window content exists was implicated in a native
`combase` `E_UNEXPECTED` crash on the first D-pad press (`MainWindow.xaml.cs:70-72`).

## Native WinUI facilities: what to adopt and what to skip

WinUI 3 desktop gives you the whole XY-focus *engine* (`FindNextElement`, strategies, overrides) and the
keyboard path. It gives you **no gamepad input source at all** — no `CoreWindow`, so no
`VirtualKey.GamepadA`/`DPadUp` ever arrives in `KeyDown`. So the hand-rolled layer is *permanently* required
for reading the pad, mapping frames to intents, auto-repeat and activation — but **not** for deciding which
element is next, which is the part currently reimplemented badly.

- **Adopt `XYFocusKeyboardNavigation="Enabled"`** on the window root. Costs nothing (it only applies when the
  focused element doesn't handle the arrow itself, so text boxes, sliders and scrollers keep their behaviour)
  and gives keyboard users the same directional model as pad users — the cheapest way to keep the modes honest.
- **Adopt `XYFocus*` overrides sparingly**, and only where geometry lies — driven by audit findings, not
  pre-wired. Prefer `XYFocus*NavigationStrategy="Projection"` on containers over hardcoded element
  references, which break silently when markup is rearranged.
- **Skip `IsFocusEngagementEnabled`** — it is defined entirely in terms of gamepad key events that never
  arrive. And don't hand-roll engagement either; it costs a mode, a visual state and a way to get stuck.
  Instead do **axis-aware intent routing**: on a `Slider`, Left/Right adjust and Up/Down move focus; on a
  `ComboBox`, South expands (the popup then becomes the search root, so directional works inside it). That
  is exactly what the keyboard already does, so it needs no new mental model. Right stick scrolls the nearest
  ancestor `ScrollViewer` — the one genuinely new interaction, and what makes `SettingsPage` pleasant.
- **Skip `FocusVisualKind.Reveal`** (the Xbox-shell glow needs template cooperation and is start-up-time, so
  switching it per input mode gives a half-repainted app). Set `HighVisibility` once, explicitly, and let the
  design system carry distance legibility through thickness/brush tokens. One focus visual for all three
  inputs is also what "one adaptive UI" means.
- **Skip `RequiresPointer`** (Xbox mouse-mode; irrelevant on desktop).
- **Adopt `TabFocusNavigation="Cycle"` on modal roots** — the native keyboard half of a focus trap, free.

**Never synthesize key events from the pad.** A `SendInput`-style pad→arrow bridge would appear to get XY nav
for free, but it would also inject arrows into the console stream and into text boxes. Both paths call the
same engine with the same options instead, so a pad `Down` and a keyboard `Down` land on the same element by
construction.

## Focus invariants, enforced centrally

1. Every scope declares initial focus (resolved after a layout pass, since a just-shown panel has no realized
   containers), focused with `FocusState.Keyboard`.
2. Every scope records the previously focused element on push and restores it on pop if still focusable —
   covering dialog dismissal, keyboard-overlay dismissal, and returning from the stream layer (which today
   restores nothing).
3. Step changes re-seed. `AddConsolePage.ShowStep` currently sets focus only for `Link` and `Done`; arriving
   at `Family`, `Find` or `Pairing` leaves focus wherever it was — typically the nav pane, or nowhere.
4. **The focused element vanishing is handled in one place**, two mechanisms because there are two shapes:
   a `FocusManager.LostFocus` watchdog that re-seeds when nothing is focused or focus escaped the scope root;
   and a `FocusAnchor` for lists that re-sort under the user — the discovery list inserts sorted, so a console
   answering late can insert **above** the focused item. Rule: re-focus **the same item's** container, not the
   same index. Corollary, equally important: **never move focus onto newly arrived items** — a discovery
   result landing under the user's thumb must not steal the press.

Debug builds assert after every scope change and every navigation that something is focused, so invariant 4
is provable rather than hoped for.

## Text entry by controller: one overlay, not per-digit spinners

A `SoftKeyboardOverlay` in its own `Popup` — so it renders above a `ContentDialog` and, being the topmost
popup, the fixed search root finds it with **zero special-casing** (the payoff for fixing the root properly).
Two layouts chosen from the target's `InputScope`: a 3×4 keypad for `NumericPin`/`Number`, compact QWERTY
otherwise (enough for an IP address and an account id). `LoginPinDialog.xaml:38` already declares
`InputScope="NumericPin"`, so the keypad is selected with no change to that file. Keys ≥56×56 so it doubles
as the touch keyboard on a handheld. Writes through `IValueProvider` where available so existing
`TextChanged` validation keeps working.

Note the pairing flow has **four** text fields (link code, account id, host, name), so `LoginPinDialog` was
never the only text-entry hole — it was the smallest one.

## Touch and keyboard

**Touch:** 48×48 minimum in chrome, 56×52 over video (what the touch bar already proves on the Ally), 8px
minimum between targets. Concrete fixes: the card's 32×32 overflow button, default-height (32) footer and
header buttons, `SettingsPage`'s combos. **Add no gestures** — they're undiscoverable and duplicate controls
that must exist anyway for the other two modes. The one exception is *confirming* that press-and-hold raises
`ContextRequested` (a platform convention, not an invention); if the `GridViewItem` consumes the hold, wire
`Holding` with `handledEventsToo: true` — the same hard-won lesson as the touch bar's pointer wiring. Rule
worth adopting: **a tooltip may only ever repeat information**, since touch never hovers (today the card's
full name lives only in a tooltip). Auto-hide stays exclusive to the video layer — in chrome there is nothing
to reveal underneath, and a control that vanishes is one a controller user cannot find.

**Keyboard:** keep declaration order, add no `TabIndex` (a maintenance trap in a codebase whose comments
repeatedly praise robustness to markup rearrangement); where order is wrong, fix the markup. Add window-root
accelerators `Alt+Left/Right`, `F6` pane cycling, `Ctrl+,`, `Ctrl+N`, and `AccessKey` mnemonics on primary
actions. Write the Enter/Escape contract down once, in a comment on `InputScope`, because it is the contract
all three inputs share.

## The affordance system

One `InputHintBar`, fed by the top scope's declared prompts, so the bar knows nothing about pages and a new
surface gets correct prompts by declaring them where it already declares its focus root. `IsTabStop="False"`,
never a target. Visible always in Controller mode, only for non-obvious keys in keyboard mode, hidden in
touch (every prompt corresponds to a visible control). Mode detection via a pure `InputModeTracker` with
hysteresis: switching *to* Controller is immediate, switching away needs a deliberate act plus a dwell, so a
bumped mouse on the coffee table doesn't strip the prompts off a couch session.

**Honouring the existing trademark position** (the touch bar deliberately uses text, not Sony's glyph
designs): we draw our own badge and put **text** inside it — never a reproduction of a vendor symbol set,
never a symbol font containing one. Badge text adapts to the detected pad family (`Cross`/`Circle` as *words*
for DualSense; plain letters `A`/`B` otherwise — a letter in our own badge is nobody's mark), and the action
verb always accompanies it (`[Cross] Select`), which is also what makes it legible at distance and to a
screen reader. Centralised in one `ButtonLabels.cs` — one file to point a lawyer at.

## Stage C order

1. **The search-root fix + activation chain (`ExpandCollapse`, `RangeValue`) + `FocusState.Keyboard`**, in
   place, no new types. This alone makes every dialog, flyout and combo navigable and makes seeded focus
   visible. **Highest value-to-risk ratio in the whole plan — land and hand-test it first.**
2. Extract `NavIntentReader` + `InputModeTracker` into `Ripcord.Core.Input` (following the clock-injected
   `ExitGestureDetector` precedent); unit tests.
3. `InputRouter` + `InputScope`; delete `_navControllerSource`, `IsCapturingInput`, and SessionPage's source
   ownership; move the remap into the router. **Fixes "no DualSense in menus" and the mid-session dialog dead
   end simultaneously.**
4. `FocusPilot` + axis-aware routing + right-stick scroll + North → `ContextRequested`.
5. Focus invariants: initial/restore, the `LostFocus` watchdog, `FocusAnchor`, `ShowStep` re-seeding.
6. `ModalHost`; convert all seven `ShowAsync` sites; `XYFocusKeyboardNavigation`; `Cycle` on modal roots.
7. `SoftKeyboardOverlay` (keypad, then QWERTY).
8. `KeyBindingsPage` replaces the dialog.
9. `InputHintBar` + `ButtonLabels`.
10. Touch pass, then keyboard pass.

**Gotcha to carry:** applying the remap in both the router and `MergedInputSource` double-swaps (it is not
idempotent), so `MergedInputSource` must be constructed with an empty remap — load-bearing, and it deserves a
comment saying why.

---

# Delivery

Feature branch, one reviewable commit per numbered step, `main` stays streamable throughout. Stage order is
**A → B-design-system → C-step-1 → the rest**, with one deliberate reordering: **Stage C step 1 (the
search-root fix) jumps ahead of the rest of Stage B**, because it is small, self-contained, unlocks
controller use of surfaces that exist today, and validates the "keep `ContentDialog`" decision that Stage B's
dialog rule depends on.

Hardware-gated work batches into single console sessions, the way ROADMAP Track A already batches: Stage A
steps 1/2/7 (credential + registration paths) in that order in one sitting; Stage B's stream work in another.

# Verification

**Automated — the logic.** `dotnet test tests/Ripcord.Protocol.Halyard.Tests/...` must stay green (currently
448 passed / 5 skipped on this ARM64 host). New `tests/Ripcord.Presentation.Tests` for the pairing state
machine and reachability monitor (~50 cases listed in Stage A). Pure input classes tested in the existing
suite beside `KeyboardInputTests`: auto-repeat timing (the regression `MainWindow.xaml.cs:28-32` was written
for), deadzone, `Reset()` swallowing the in-flight rising edge, mode hysteresis, scope-stack ordering.

**Automated — the structural guards.** These are what keep the plan's outcomes true afterwards, and the repo
already governs itself this way (`BundledInteropConstantsTests.Bundle_CarriesNoLiveVectorMaterial` is the
precedent): the portable layer references no UI framework assembly; no `<Style>` in a page's resources; no
`Storyboard`/`ConnectedAnimationService` outside `AppMotion`.

**In-app audit instead of UI-automation infrastructure.** A `FocusAudit` bound to F9 walks every focusable
element using *the same* `FindNextElement` + automation-peer calls production uses, so a finding is a real pad
failure rather than a model of one. It reports: focusable-but-not-activatable (would have caught the 6 combos
and 2 sliders), unreachable islands, dead ends, missing automation names, sub-48px targets, tooltip-only
information, and a search-root mismatch while a popup is open (a permanent regression guard on the headline
bug). Findings append to the existing F8 report — which exists precisely so a handheld with no debugger can
be diagnosed.

**Hands-on, with the other two inputs physically denied** — because "it works when I also have a mouse" is
exactly the failure the requirement is about. Worth building the ~10-line debug switch that drops pointer and
key input at the window root, so the pad-only pass is repeatable.

Per surface, per input, four invariants — a row is either four ticks or a named defect:

```
[ ] REACH    every interactive element can be focused/activated with this input alone
[ ] ACT      once reached, every element performs its action (value controls change value)
[ ] ESCAPE   the surface can be left with this input alone
[ ] VISIBLE  focus or pressed state is always visible, legible at 2 m, never absent
```

Surfaces: Home (empty, one-console hero, populated, overflow flyout, rename, details, remove, error); Pair
(all five steps, manual address, **and a discovery list re-sorting mid-navigation — do this deliberately by
powering on a second console during the scan**); Settings (all 6 combos, both sliders, 9 toggles, scrolling
past the fold); Controls incl. capture mode; About; Session (connecting, streaming, degraded, failed/retry,
diagnostics open, touch bar, fullscreen and windowed); login PIN + soft keypad; **disconnect dialog
mid-live-session, entered via the exit gesture — the couch case**; and first launch with focus never touched.

Two hardware axes: pad family (**DualSense over raw HID only** — the case today's GameInput-only chrome
source cannot see; Xbox pad; the Ally's built-in controller; two attached and hot-swapped) and display
(handheld 7", desktop monitor, **TV at 2–3 m for the focus-ring legibility judgment the one-adaptive-UI
trade-off rests on**).

**Regression baselines to capture before starting:** screenshots of the console grid in light/dark/high
contrast including hover and focus states (Stage A step 5 is the highest visual-regression risk), and a
working stream with the diagnostics overlay open.

# Open questions to file, not guess

1. **Does the console validate the PSN account id at registration, or merely record it?** Decides whether the
   account-id step is permanent or deletable. Cheap experiment: pair with a well-formed but wrong id and see
   whether registration succeeds *and* the stream comes up.
2. **`ClientDeviceId` is 32 random bytes where its own doc says 16.** Carry over byte-for-byte; mark `[X]`.
3. **Is `UISettings.TextScaleFactor` genuinely unapplied in WinUI 3 desktop?** Verify empirically at 150%
   before building the scaling work.
4. Whether the `CommunityToolkit` settings controls survive ROADMAP Track B's `PublishTrimmed` goal — measure
   before committing to the first non-SDK package reference.

Also still open from earlier today, deliberately tabled by the user: whether the UI pairing run used the new
bundled-constants fallback path, and the PS4 login-PIN entry failure (which lives on `LoginPinDialog` — a
surface this plan rebuilds, so the rebuild should verify it rather than assume it is a crypto fault).
