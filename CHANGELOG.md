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
