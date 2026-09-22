# OVERSEER Implementation State

## Current
Plan: P2 — Complete Usable HUD
Goal: G2 — Usable persistent website-derived desktop HUD
Branch: impl/p2-hud

## Plan Status
- Astra handoff: ready
- DeepSeek implementation: complete on this branch (5 logical commits, see Completed)
- Build: passed (0 warnings, 0 errors)
- Tests: passed (215 passed, 0 failed, 0 skipped: 139 reviewed P1 cases + 76 new)
- Smoke: passed (HUD smoke exit 0, live HUD run exit 0, monitor regression exit 0)
- Astra review: pending (Review 2)

## Completed

Base is reviewed P1 commit `7c665609c36d8d86c2ff6d09b5aacf596c3937ed` (= local main).
Planning head was `e8ee9518b31c08a889f3f93fd3617d81d64e0bed` (P2 ledger only). No bootstrap
or P1 merge was repeated; frozen documents, reference PNGs and project names are
untouched, and no package, project, database or framework was added.

| Commit | Scope |
| --- | --- |
| `feat(core)` | `HudSettings`, `HudWindowState`, bounded atomic `HudStateStore`, framework-free `HudPlacementPolicy`, additive `hud` config section |
| `feat(ui)` | `HudViewModel`/`TaskRowViewModel`/`HudText`/`RelayCommand`/`DragGestureTracker`, one `HudWindow`, `HudThemeManager`, WebsiteLight + WebsiteDark dictionaries; scaffold `MainWindow` removed |
| `feat(app)` | `HudApplicationController` (composition, coalesced delivery, persistence, lifecycle), `SnapshotPump`, tray, placement service, startup registration, task navigator, single-instance guard, `app.manifest`, normal-mode arguments and fixtures |
| `test` | HUD view model, dispatch/coalescing, settings/state, placement, navigation, startup, window/theme and lifecycle coverage on an STA host |
| docs | README and this ledger |

### Implemented behaviour

- Normal startup runs exactly one monitor + window + tray through
  `HudApplicationController`. `--monitor-debug` keeps its finite, window-free
  behaviour and exit codes 0/1/2. The UI thread never scans, parses or blocks.
- `SnapshotAvailable` (a producer callback under the monitor's cycle gate) only
  stores the newest snapshot and posts at most one dispatcher delivery;
  `SnapshotPump` proves 50 producer snapshots become one applied update, and that
  a snapshot arriving during apply gets exactly one more delivery. Applying a
  snapshot never touches window state, so passive updates cannot activate the HUD.
- The HUD renders the monitor's own `CountValue`/`MonitoringHealth`/
  `MonitorCapabilities`: unknown shows `?`, partial shows `N+` with a `partial`
  qualifier, healthy-empty and unavailable have distinct copy, stale/provisional
  rows are marked, and a non-blocking request stays WORKING while disclosing
  "needs you soon". Counts are never recomputed from the task list.
- One `HudWindow` tree serves collapsed (280×72 DIP) and expanded (360×392 DIP,
  list capped at 244 DIP with a themed scrollbar) modes and both palettes;
  switching theme swaps the merged resource dictionary and keeps expansion and
  layout. Decoration is static geometry (thin rules, clipped corner) with hit
  testing disabled.
- Row order is deterministic (attention states first, then stable task number) and
  independent of age, so the 1 s age timer can never reshuffle rows. Task identity
  is the `TaskKey`; labels fall back to `Task #N`.
- Placement is computed by a framework-free policy: saved DIP offsets + saved DPI
  are converted to physical pixels, clamped fully inside the target working area,
  and fall back to the primary monitor when the saved monitor is gone. WPF owns
  the DIP size and the policy clamps the measured rectangle, so the window is
  always fully reachable and never clipped.
- Persistence is bounded and serialized: `config.json` keeps every monitor field
  and adds `hud`; `state.json` holds placement + expansion (16 KiB read cap,
  corrupt/oversized fallback, atomic temp+move). Drags are debounced; shutdown
  flushes.
- Tray offers Show/Hide, Light/Dark, Always on top, opacity presets, Start with
  Windows, Reset position, monitoring status and Exit. If tray creation fails the
  HUD stays reachable and the failure is disclosed. A second normal launch exits
  quietly (named mutex) and never creates another monitor or tray.
- Startup is an off-by-default current-user `OVERSEER` value with a quoted real
  apphost path plus `--start-in-tray`; the HUD never enables it by itself, shows
  the real registry state, and reports denied/stale paths honestly. `--start-in-tray`
  produces no visible window while the tray and monitor stay alive.
- Navigation validates surface, provisional identity, profile and identifier, then
  requests `codex://threads/<escaped id>` through `UseShellExecute` with no shell,
  no task control and no clipboard mutation unless the user explicitly copies an id.
- Exit unsubscribes the producer, cancels, awaits bounded disposal, saves,
  disposes the tray and closes the window, logging `hud-shutdown-complete`.

### Verification evidence

```text
dotnet restore / build / test  -> build 0 warnings 0 errors, tests 215/215 passed
--monitor-debug --duration 60  -> exit 0; 65 conversations; live-observed lifecycle
                                  (245 starts / 281 completions / 9 aborts), item
                                  activity (8,566), tool calls (7,142), titles and
                                  optional state-store enrichment; 64 tracked files,
                                  60,891,116 bytes; approval/input unsupported;
                                  attention-live not exercised
--hud-smoke --duration 14      -> exit 0; window visible, tray available failures=0,
                                  theme toggled live to light, opacity 85%, state and
                                  config persisted, "hud shutdown: clean"
                                  wpfSize=280x72 DIP -> hwndRect=350x90 physical at
                                  dpi 1.25 (DPI-correct rendering, no clipping)
live HUD run (real monitor)     -> window visible at the saved position; foreground
                                  window unchanged; second launch exited 0 quietly
                                  while the first kept running; close request exited 0;
                                  state.json/config.json written; log ends
                                  "hud-shutdown-complete"
--start-in-tray                -> no visible windows, foreground unchanged, clean exit 0
--hud-startup-check            -> Disabled -> Enabled -> verified -> Disabled ->
                                  original state restored exactly, exit 0
```

Synthetic previews were rendered to ignored `artifacts/p2-ui` (light/dark ×
collapsed/expanded plus healthy-empty and unavailable) and the four required views
were visually inspected: legible, unclipped, palette-correct, no dashboard, no
copy of the reference screenshots.

Sandbox note (unchanged from P1 review): workspace child processes cannot read the
user-level NuGet config, so restore used the ignored offline config
`artifacts/nuget-offline.config` with build servers and node reuse disabled. No
repository file depends on it.

### Acceptance criteria A1–A9

- A1 pass: normal mode uses the reviewed `CodexMonitor` off the UI thread;
  `--monitor-debug` semantics and exit codes unchanged.
- A2 pass: collapsed/expanded inside desktop bounds; complete/partial/unknown,
  stale/provisional, task-error vs monitor-failure and unsupported attention are
  visibly distinct.
- A3 pass: rows keep `TaskKey`/`Number` and the monitor's action label; a synthetic
  Working + NeedsHuman case keeps both facts; healthy-empty differs from offline.
- A4 pass: one window tree, matching Light/Dark keys (§9 palettes), static
  decoration, both modes/themes inspected.
- A5 pass: drag/expand/theme/topmost/opacity/persisted position work; no activation
  from passive updates; no I/O on the UI thread; task state untouched by UI actions.
- A6 pass: tray, opt-in startup, settings persistence and clean shutdown verified
  live; tray failure keeps the window reachable; second launch is inert.
- A7 pass: Desktop-only validated route; CLI/VS Code/provisional/profile cases and a
  missing `codex:` handler all produce honest fallback; nothing was executed.
- A8 partial by hardware: synthetic layout/DPI/removal tests pass and real 125%
  per-monitor DPI behaviour was verified; secondary-monitor, monitor-removal and
  sleep/wake were **not** exercised (single display available) and remain unverified.
- A9 pass: 139 existing tests still pass, 76 new tests added, Core stays
  presentation-independent, no frozen file, P3/P4 feature or dependency drift.

## Known Limitations
- Approval/input attention remains **unsupported** on this Codex version (transient
  inside the owning runtime). The HUD displays that capability honestly instead of
  treating a zero needs-you count as proof that nothing is waiting.
- `codex:` has **no registered handler** on this machine (`HKCR\codex` has no
  `shell\open\command` and no `URL Protocol` value), so a task click reports the
  fallback; no task was launched and exact-focus behaviour is unverified.
- Secondary monitor, monitor removal, mixed-DPI and sleep/wake paths have synthetic
  test coverage only; the live run used one 2048×1152 display at 125% scaling.
- The HUD deliberately has no history chart, sparkline, mute or voice controls;
  those belong to Plan 3 and the space shows a compact health/metadata strip.
- Window geometry on heavily scaled displays follows WPF's layout scale: the
  requested 280×72 DIP becomes 350×90 physical at 125%, and the placement policy
  clamps the measured physical rectangle.
- Tray menu interaction (clicking items) was exercised only through the controller
  API and the smoke fixture, not by a human clicking the icon.

## Blocking Issues
- none

## Next Action
- Astra Review 2 reviews `main...impl/p2-hud` (build/tests/smoke above). Do not
  merge, tag or push; P3 (history + voice) starts only after that review passes.
