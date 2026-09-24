# OVERSEER changelog

## Unreleased — 2026-09-24 (project-centered round, partial)

### Added

- Project identity and a deterministic project resolver (explicit mapping, configured
  root, verified Git root with worktree grouping, workspace root, otherwise
  Unassigned) with complete-segment path comparison, bounded ancestor checks and a
  bounded cache. Roots stay local metadata: diagnostics emit a name and stable hash.
- Harness-qualified task keys (harness + stable source instance + conversation), so
  equal conversation ids in different harnesses remain separate tasks.
- A compiled internal `IAgentMonitorAdapter`/`SourceSnapshot` contract with
  per-source health, coverage and capabilities.
- Bounded observed activity history per task and project: confirmed/inferred/unknown
  separation, explicit unknown coverage instead of idle, markers that keep the
  underlying interval, per-interval project attribution, union-based project strips
  and bounded 720-bin geometry. 27 new tests cover these boundaries.

### Changed

- Closing the HUD window (×, Alt+F4, system menu) now hides it to the tray while a
  working tray exists. **Exit** still quits, Windows logoff/shutdown is never
  cancelled, and a missing tray keeps the window reachable instead of hiding it.
- A second normal launch now asks the running instance to restore its window and
  session before exiting quietly.

### Integrations (external assets, opt-in)

- `integrations/claude-code/`: a documented-hook bridge plus preview/backup
  install and uninstall helpers. Contract-tested (19 Node tests) but **not**
  live-qualified — Claude Code is not installed on this machine.
- `integrations/deepseek-harness/`: a small host-native observer plugin that exports
  allowlisted status metadata from the user's existing `dsh` host. Tested in
  isolation; not yet attached to a running host, and OVERSEER does not consume the
  spool yet.

### Not yet implemented

- Project-first HUD rows, activity strips, responsive header/menu refinement, the
  composite multi-source monitor, the dsh/Claude adapters inside the app, and the
  offline baseline voice. Details and next steps: `docs/IMPLEMENTATION_STATE.md`.

## Unreleased — 2026-09-23

### Fixed

- Upper-right controls, their gaps and health indicator now use the normal arrow
  cursor and never start a window drag. Popup menus also enforce the arrow cursor.
- Active-row timers now measure elapsed time for the current turn, instead of
  resetting with each action. Last-activity freshness remains in the tooltip;
  an unobserved turn start displays a dash rather than an invented duration.
- Active tasks could disappear from the live count after Codex image-view or
  model-context state records. OVERSEER now recognizes these records without
  reading their private content. Unknown records still disclose incomplete coverage.
- Dragging works throughout the upper summary and expanded status area. A move
  cursor marks these areas; buttons and menus retain their normal cursors.

### Changed

- Resizing, maximize/restore and Windows Snap now retain OVERSEER's own HUD
  header. Added a separate maximize button with real Snap hover, an explicit
  menu button and compact/expanded control. Edges resize, header background
  drags, and controls/light grips retain their own interactions. Compact resize
  promotion waits for a committed gesture; canceling preserves saved larger bounds.
  Compact and larger layouts preserve separate bounds and pin preferences while
  keeping the existing monitoring/voice session alive.
- Window layouts use extra height for tasks, scroll in short regions and preserve
  readable content beside the light. Light changes no longer resize a larger HUD.
  Activity graph strokes keep their thickness as width changes; settings remain
  scrollable on smaller displays. Added scale-policy and native-window checks.
- Added a thin black outline to the white emblem for contrast on light surfaces.
- Replaced the app, taskbar and tray icon with the supplied white standalone
  OVERSEER emblem; preserved the original artwork and transparency.
- Added a visible LIGHT ON / LIGHT OFF toggle directly on the HUD header, beside
  mute. Doubled the light's maximum size to 128 DIPs for dragging, slider and presets.
- Surfaced Status light directly in the HUD menu with position/width presets.
  It now defaults to the right at 32 DIPs wide, with sizes up to 64 DIPs.
  Drag the light directly to resize it, with a direction-appropriate cursor.
- Added Light & voice settings in the header and tray menus: an optional steady
  status strip (four sides, adjustable size), nine voice situations with trigger
  descriptions, per-event switches, WAV assignment, validation and explicit preview.
- Added sustained monitoring-loss/restoration cues, an optional five-minute waiting
  reminder and an optional startup greeting. Reminders and startup default off;
  brief outages, user cancellation, normal UI actions and notification backlogs
  stay silent. Restoration requires successful playback of the loss cue.
- Assigned recordings are copied locally and override the configured pack/TTS for
  that event. Preview works while muted; Stop, close and mute cancel playback.
- Applied the supplied OVERSEER artwork to the executable, taskbar/window and tray
  icons, with standard Windows sizes and preserved transparency.
- Replaced diagnostic headlines with concise status text. A compact coverage/input
  label replaces the persistent disclaimers; full explanations remain in tooltips
  and the header's Monitoring details menu. Uncertain counts remain marked.
- Reduced the graph caption to small, muted, regular-weight text that shrinks to
  fit narrow layouts without enlarging in wider ones.
- Task rows share the HUD status palette through a thin accent and colored status
  text: green for work, red for attention/errors, amber for stale/uncertain rows,
  and neutral for settled tasks. Titles retain their normal text color.
- Larger status text: green for working, red for confirmed attention/errors and
  amber for uncertain activity. Unread history and stale updates are explained.
- The expanded graph is labeled **Working tasks · past hour**, with a task-count
  scale and **60 min ago → Now** axis. Dashed lines mean minimum known counts;
  gaps mean no observation. The unlabeled miniature graph was removed.
- Always-on-top remains enabled by default and has been verified on the running
  Windows app. The setting remains user-controlled.

## Unreleased — 2026-09-22

- Added Windows taskbar and Alt+Tab visibility without automatic focus stealing.
- Added **Now**, **Recent** (1–24 hours), and **This session** task views, with
  saved preferences. Filtering leaves global monitoring counts unchanged.
- Added prominent visual status and a status click that opens current tasks.
- Unsupported approval/input detection displays **?** or a known lower bound,
  rather than claiming zero requests. This detection limitation remains.

Detailed test results, package provenance and remaining qualification work are
recorded in [the implementation ledger](docs/IMPLEMENTATION_STATE.md).
