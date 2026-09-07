# Superseded plans

Documents here described work that has since landed. They are kept because they record *why* decisions
were made, which the code cannot show, and because the dated trail matters to this project's clean-room
position. **None of them is current.** For where things actually stand:

- what is left to do → [`ROADMAP.md`](../../ROADMAP.md)
- what happened → [`journal.md`](../journal.md)
- how the code is arranged now → [`architecture.md`](../architecture.md)

| Document | Written | Covers | Outcome |
|---|---|---|---|
| [`phase1-lan-build-plan.md`](phase1-lan-build-plan.md) | 2026-07-13 | Turning the protocol specs into an ordered, buildable sequence for LAN remote play, structured around the crypto seam | Delivered. Phase 1 works end to end against real hardware. |
| [`app-reimagining-plan.md`](app-reimagining-plan.md) | 2026-08-05 | Stages A (portable extraction), B (design system + IA) and C (input independence) | Delivered, with hardware-verification items still open — see the roadmap. |
| [`app-reimagining-test-plan.md`](app-reimagining-test-plan.md) | 2026-08 | Round-2 manual test plan for the above | Executed; findings folded into the roadmap and journal. |

The build plan is still the best explanation of *why* the crypto seam exists, and
[`../protocol/IMPLEMENTATION.md`](../protocol/IMPLEMENTATION.md) — which is current — carries the build
order forward.
