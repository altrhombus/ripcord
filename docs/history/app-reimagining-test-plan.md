# Ripcord app re-imagining — test plan (round 2)

> **Superseded — kept for the record.** This plan describes work that has since landed; it is not a
> current description of the system. See [`../../ROADMAP.md`](../../ROADMAP.md) for what is left,
> [`../journal.md`](../journal.md) for what happened, and [`../architecture.md`](../architecture.md)
> for how the code is arranged now.

Covers `main..HEAD` on `feat/app-reimagining`, now through `b0b925b`. Round 1's findings are folded in:
passes are struck through, failures are re-listed with what changed.

**Round 1 was worth it.** One report — "the controller stops responding and there's a tooltip on screen" —
turned out to be a single bug wearing four costumes, and it had also been masquerading as the slider trap that
three of my fixes failed to solve. The most useful things you wrote were the incidental observations, not the
pass/fail marks.

## How to read the marks

| Mark | Meaning |
|---|---|
| ~~struck through~~ | Passed on hardware. Skip. |
| 🎮 | **Passed on an Xbox pad, still open for DualSense.** Different engine entirely — the DualSense comes in on our own raw-HID path, the Xbox pad through GameInput. A pass on one says little about the other. |
| 🔁 | Failed in round 1, changed since, needs a retest. |
| 🔴 | Never run, and I expect trouble. |
| ⬜ | Never run, no specific suspicion. |
| ⚠️ | Known broken or known-unfixed. Confirm only. |

**Please run §1 twice** — once per pad — and note which. For everything else, one pad is enough unless it
carries 🎮.

---

## §0 Setup

- ~~**0.1** App launches to Consoles, chrome drawn.~~
- ~~**0.2** One pad at a time; both pair-before-launch and pair-after-launch exercised.~~
- ~~**0.3** No `input-probe.log` created.~~ *(Re-check once: a probe was added and removed again this round.)*

---

## §1 The pad, on a DualSense

Everything here passed on an Xbox pad. The DualSense reaches the app through a completely different engine,
so none of it is settled.

- **1.1** 🎮 Launch with the DualSense attached. A console card has a visible focus ring without any press.
- **1.2** 🎮 Left/Right/Up/Down move focus around the console grid.
- **1.3** 🎮 **Cross** starts a connect; **Circle** goes back a page.
- **1.4** 🎮 **Triangle** opens the console card's menu; **Circle** dismisses it and focus returns to the card
  you opened it from.
- **1.5** 🎮 In the menu, arrowing down reaches Rename → Details → Remove in three presses.
- **1.6** 🎮 Up from the grid reaches the title-bar Settings/About buttons; Down returns to the page.
- **1.7** 🎮 Settings: Cross opens a ComboBox, arrows move inside, Cross picks, Circle closes.
- **1.8** 🎮 Right stick scrolls a long page (Settings).
- **1.9** 🔴 **The hint bar must say `Cross` / `Circle` / `Triangle` — words, in our own badge.**
  If you see a drawn ✕ ○ △ □ anywhere, stop and tell me: that is a trademark rule failing, not a cosmetic bug.
  On the Xbox pad it should read `A` / `B` / `Y`.
- **1.10** ⬜ Swap pads mid-session (unplug one, connect the other). The badges should follow the new pad.

---

## §2 Retests — things that failed in round 1

- **2.1** 🔁 **The soft keyboard.** Add console → manual address entry → focus the **host address text box**
  → press Cross/A. A QWERTY keyboard should appear over the page.
  - **Be sure focus is on the text box itself, not the "Enter an address" button.** Round 1 may have tested
    the button; a Button is not a text field and correctly does something else.
  - Arrows move between keys, Cross/A presses one, Circle/B closes it.
  - Closing returns focus to the text box.
  - Type a full IP address. Report whether it is *tolerable*, not merely whether it works.
  - **This is a fix for the most likely cause, not a confirmed one.** If no keyboard appears, say so and I
    will instrument the path rather than read it again.
- **2.2** 🔁 Same on the **link code** and **name** fields — QWERTY both.
- **2.3** 🔁 **Two controller actions to break out after a tooltip.** Hover the mouse to raise a tooltip, then
  press a direction on the pad. It should move on the **first** press now. (Activation still costs a press
  after focus has been re-seeded — that is deliberate, so Select cannot act on something you have not seen.)
- **2.4** 🔁 **The Y/Triangle menu opens with its first item selected**, and the first Down goes to the second
  item, not the third.
- **2.5** 🔁 **Escape goes back** from Settings, About, Add console, and Keyboard controls.
- **2.6** 🔁 **During a stream, the title bar's Settings/About/Back are gone.** The window controls and the
  drag region must remain — you should still be able to move and close the window.
- **2.7** 🔁 **The add-console page focus order.** Walk the Find step top to bottom. Focus should progress
  through the results list, Search again, Enter an address — not jump to the title bar or skip a control.
- **2.8** 🔁 **The reconnect loop.** Start a stream on a console, then put the console into rest mode from the
  console itself so it drops mid-session. Ripcord should retry a bounded number of times with a visible
  backoff and then **fail with a message mentioning rest mode** — not loop forever. This is the one that
  needed an app kill in round 1.
- **2.9** ⬜ While you are there: does the status ever say **waking** when a console is asleep, or does it go
  straight to "Reconnecting"? Round 1 said the latter. I have not changed the wake path.

---

## §3 Confirmed working — spot-check only if something near them breaks

- ~~**3.1** Existing console streams; H.264 came up twice.~~
- ~~**3.2** Diagnostics overlay (F8) reads correctly.~~
- ~~**3.3** Settings save and survive a reopen; Settings does not crash; capability text populates.~~
- ~~**3.4** Quit and relaunch: consoles, settings and last-played survive.~~
- ~~**3.5** Scan completes and the progress bar disappears; abandoned scans stop; empty scan opens manual entry.~~
- ~~**3.6** Pairing flow completes end to end.~~
- ~~**3.7** Focus returns when clicking the app's empty space.~~
- ~~**3.8** Focus stays put when pressing against the edge of the grid.~~
- ~~**3.9** Every dialog is answerable by pad (rename, details, remove).~~
- ~~**3.10** Keyboard controls is a page; rebinding keeps focus on its row.~~
- ~~**3.11** Bitrate and deadzone sliders: Left/Right adjust, Up/Down leave to the adjacent setting.~~
- ~~**3.12** Hint bar returns immediately when the pad is nudged.~~

---

## §4 Never run yet — key bindings and keyboard

- **4.1** ⬜ Rebind an action to **Enter** (should bind) and to **Escape** (should be refused as reserved, and
  stay armed for another key).
- **4.2** ⬜ Backspace while armed clears a binding.
- **4.3** ⬜ Bind a key already in use — you should get a message naming what it was taken from.
- **4.4** ⬜ Reset to defaults restores everything.
- **4.5** ⬜ **Enter on the Keyboard controls page re-presses the focused Change button.** That is correct
  WinUI behaviour, not the old data-loss bug. What I want to know is whether you can *see* what is focused —
  round 1 said no, because clicking with a mouse draws no focus visual.
- **4.6** ⬜ Accelerators: `Alt+Left`, `Ctrl+,`, `Ctrl+N`, `F6`, `Alt+S`, `Alt+A`.
- **4.7** ⬜ Tab through Consoles and Settings. Order follows the layout; nothing skipped, nothing trapped.
- **4.8** ⬜ Hint bar with a **keyboard** as the last input: only `Menu → Options` should show.
- **4.9** ⬜ Hint bar during a stream: **nothing**, by design — every button belongs to the game.
- **4.10** ⬜ Put the pad down, click with the mouse: the bar disappears after ~700ms. Re-time this now that
  tooltips no longer block input.

---

## §5 Never run yet — layout and design

- **5.1** ⬜ One paired console → hero layout, verb reads **Play**.
- **5.2** ⬜ Two or more → grid layout.
- **5.3** ⬜ Zero → empty state offering Add.
- **5.4** ⬜ Connected animation carries the card into the session and reverses on the way out.
- **5.5** ⬜ Resize to ~1280 and narrower: cards reflow, nothing clips, no horizontal scrollbar.
- **5.6** ⬜ Exactly one "Add a console" affordance (the ghost tile).
- **5.7** ⬜ No card has a drop shadow anywhere.
- **5.8** ⬜ About page renders with version/licence chips.
- **5.9** ⬜ **Console cards grow taller once a scan completes** — reported in round 1, not yet investigated.
  Confirm it still happens and say whether it is the card or the text inside it that changes.

---

## §6 Needs a console

- **6.1** 🔴 **Pair a console end to end by controller only** — no keyboard, no mouse. The real acceptance test
  for §2.1–2.2, and the scenario the whole input stage exists for.
- **6.2** ⬜ Wake a console from rest mode via its card.
- **6.3** 🔴 **PS4 login-PIN entry.** Previously suspected to be a crypto fault; it lives on a surface this
  work rebuilt, so this run tells us which it was. Expect a **numeric keypad**, above the dialog.
- **6.4** ⬜ H.264 session + F8: the confirming evidence is a decoder line reading H.264 with **no**
  "overriding the H.264 request" suffix. Two successes without checking this line is not conclusive.
- **6.5** 🔴 **Mid-session dialog.** Trigger the disconnect prompt with the pad. It must be answerable, and
  whatever you were holding when it opened must not stay held inside the game. Round 1 left the app needing a
  relaunch here; the cause (Back closing the dialog's smoke layer instead of the dialog) is fixed.
- **6.6** ⬜ Session HUD: capability pills, stats, touch bar.
- **6.7** ⬜ Exit gesture from the pad leaves cleanly.
- **6.8** ⬜ Leave a session without resting the console — the card should show ready/settling correctly.
  Round 1 said it showed "ready" immediately, which may be right or may be too eager.

---

## §7 Needs a touchscreen

- **7.1** 🔴 **Press-and-hold a console card** opens its menu. Deliberately not pre-wired — if it does not
  work, wire `Holding` with `handledEventsToo: true`.
- **7.2** ⬜ The card's `…` button is comfortably hittable (48px hit area, 32px glyph — intentional).
- **7.3** ⬜ Settings' ComboBoxes are comfortably hittable (48 tall now).
- **7.4** ⬜ Title-bar Settings/About are 40px, knowingly under the 48 minimum. Hittable in practice?
- **7.5** ⬜ With touch as the last input, the hint bar is hidden.
- **7.6** ⬜ The soft keyboard doubles as the touch keyboard — keys are 56px for this reason.

---

## §8 Accessibility and appearance

- ~~**8.1** High contrast, Consoles page.~~
- **8.2** 🔴 **High contrast, session HUD.** The only surface using accent fills and in-app acrylic, and the
  acrylic fallback is decided in code rather than by the dictionary — so this is where a hardcoded brush would
  show. Needs a live session.
- **8.3** ⬜ High contrast: Settings, About, Add console, Keyboard controls, and the **soft keyboard**.
- **8.4** 🔴 **Transparency off.** Every acrylic surface must fall back to something solid and readable. The
  soft keyboard picks an acrylic brush with a solid fallback that has never been exercised.
- **8.5** ⬜ Turn transparency off while running — the backdrop should re-decide live.
- **8.6** ⬜ Reduce motion: connected animation and transitions should not play.
- **8.7** ⬜ Text scale 150%. Expect problems; report what breaks rather than whether it is perfect.
- **8.8** ⬜ Narrator on Consoles: cards announce name and state, `…` announces meaningfully, the hint bar
  reads as sentences ("Cross: Select").

---

## §9 Known issues — confirm only, do not diagnose

- **9.1** ⚠️ **Xbox pad dead while a DualSense is attached.** Pre-existing, filed with cause.
  **Relevant to §1:** test one pad at a time or this will contaminate everything.
- **9.2** ⚠️ **HDR reported from the wrong display.** Confirmed cause: the probe asks "is ANY display HDR"
  rather than "is the display this window is on HDR". Needs a native change; filed, not attempted.
- **9.3** ⚠️ **No focus visual after a mouse click.** WinUI draws none for pointer focus. Not yet addressed.

---

## Reporting

Three lines per failure: **what you did**, **what happened**, **what you expected**. For focus problems,
where the highlight was *before* and where it went matters more than anything else.

Incidental observations have been worth more than the pass/fail marks so far — keep making them.
