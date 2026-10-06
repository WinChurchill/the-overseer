# OVERSEER UI specification (implemented contract)

This document states the HUD presentation contract that the post-V1 usability round
implements. It is a description of delivered behaviour, not a design proposal. The
fuller release rationale lives in `docs/RELEASE_READINESS.md`; the frozen V1.2
specification still governs monitoring, history and voice semantics.

## Reading order

1. **Pinned header** — product name (collapses first at narrow widths), light toggle,
   mute, expand/collapse, maximize, menu and close. Every control keeps a 24x24 DIP
   minimum, an accessible name and ordinary pointer behaviour; the background drags
   the window and can never steal a control's click.
2. **One summary** — attention first, then working work: `3+ Needs you · 4+ Working`.
   Each half is a button: *Need you* opens the attention filter (widening to the
   Recent window when the current view would hide a known attention row), *Working*
   opens the working filter. Counts always describe **all** monitored work, never the
   filtered subset, and lower bounds stay qualified (`3+`, `?`).
3. **One health control** — a single clickable line in the pinned area with a state
   dot and a concise label: *Connecting*, *Live*, *Limited coverage* or
   *Monitoring offline*. It opens per-source detail: each reporting source, its
   reason codes, evidence freshness, the aggregated capability wording and one useful
   next action. Source-prefixed reasons (`dsh:source-silent`) are matched by code, so
   a degraded bridge is never displayed as healthy.
4. **View selector** — Now / Recent / This session, preserving the persisted setting.
   The Recent window is an explicit 1/2/4/8/24-hour choice.
5. **Filters** — All / Needs you / Working, with *All* always visible as the reset and
   the active chip marked. A filter hides rows; it never changes counts.
6. **Projects** — ordered attention, then working, then recent, with the opaque project
   identity as the deterministic tie-break. First appearance expands attention/active
   projects and collapses settled ones; the user's choices are preserved. A genuinely
   new attention episode reveals its project once without focus, without changing the
   view and without scrolling.
7. **Task rows** — ellipsized title, aligned shared number column, harness badge plus
   one concise status word, compact elapsed time (last-activity age for settled rows;
   tooltip names the meaning), and a separate *History* control shown on hover, keyboard
   focus or while open. Clicking the row keeps its existing
   activation behaviour; *History* never activates the task. Marks (stale, provisional,
   cancelled, failed) stay visible; ids, evidence and full source identity live in the
   tooltip, the details text and the copy action.
8. **History disclosure** — per project and per task, closed by default. The shared
   hour axis and legend appear with the first open disclosure. Unknown intervals stay
   distinct from confirmed idle and partial/inferred segments keep their labels.
   Closing a history stops the geometry projection only: observation, recording and
   voice are never affected.
9. **One scrolling surface** below the pinned controls; no horizontal scroll and no
   nested scrollers. Narrow titles and group metadata use an ellipsis; the full
   metadata stays in the tooltip. Labels retain their font size.
10. **Missing harness** — the HUD never fabricates work. The empty state names the
    situation and points at *Projects & sources*, where a discovered source can be
    enabled or disabled additively.

## Visual rules

- Neutral rows with small state accents; strong colour is reserved for attention, and
  text always carries the same meaning as the colour.
- The optional resizable status light keeps its persisted position and width
  (6..128 DIP); while it is visible the redundant left state accent is hidden.
  Its toggle names the status; both themes use the same colour meaning. Voice uses
  a microphone with an enabled accent or muted slash. Successful navigation/copy
  confirmations expire after five seconds; failures stay visible.
- Readable text targets: 13 DIP body, 12 DIP secondary, 18 DIP summary counts.
- The collapsed HUD is 300x96 DIP so the pinned header never clips a control.
- Removed: the duplicate status headline, the decorative linework canvas, the constant
  voice sentence and the permanent interaction-instruction line. A voice failure stays
  discoverable when notifications were explicitly enabled.

## Verification

Rendered by `--hud-fixture` in both themes at 300/360/480/720 content DIPs, short and
tall, with the light at maximum width, the attention filter applied and a history
disclosure open. Behaviour is pinned by the WPF and view-model test suites; the
production path (real readers, composite, controller and applied UI state) is pinned
by `--hud-integration-check`.

## Menu and settings (2026-09-29 remediation)

The header has eight leaf actions and no nested preset menus: Always on top,
Expand all projects, Collapse all projects, Compact / expanded HUD, Light & voice,
Projects & sources, Hide to tray and Exit. The tray retains restore, pin, mute,
the two settings surfaces, status and Exit. Their pin/mute/availability state is
derived from one controller snapshot, including the separate window-view pin.

Theme, opacity, status light, startup, reset and minimize live in Light & voice.
Recent hours remain next to the Recent view selector; monitoring details remain
on the health control; maximize/restore remains on the header. Startup reflects
the confirmed registration result, including a refused change. The unused
count-chart control and projection were removed; project/task hour strips are
the only history rendering and their recording/quality semantics are preserved.
