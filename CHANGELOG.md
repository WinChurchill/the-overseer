# OVERSEER changelog

## Unreleased — 2026-09-23

### Fixed

- Active-row timers now measure elapsed time for the current turn, instead of
  resetting with each action. Last-activity freshness remains in the tooltip;
  an unobserved turn start displays a dash rather than an invented duration.
- Active tasks could disappear from the live count after Codex image-view or
  model-context state records. OVERSEER now recognizes these records without
  reading their private content. Unknown records still disclose incomplete coverage.
- Dragging works throughout the upper summary and expanded status area. A move
  cursor marks these areas; buttons and menus retain their normal cursors.

### Changed

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
