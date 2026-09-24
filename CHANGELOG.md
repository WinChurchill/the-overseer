# OVERSEER changelog

## Unreleased — 2026-09-24 (project-centered round, partial)

### Astra integration review fixes

- Prevent disabled/unreported sources from producing misleading complete counts;
  forward transitions once and derive all-complete across every monitored source.
- Find Claude's default spool, forward fresh bridge attention without replaying attach
  backlog, preserve bridge turn clocks, and treat stale/incomplete lanes as partial.
- Preserve reader byte offsets on malformed UTF-8; guard bridge source identities,
  task-number hashing, Codex navigation, and concurrent project-cache access.
- Full multi-harness recovery, settings, speech lifecycle and release validation
  remain unfinished; see the latest implementation-state review.

### Continuation round — bridge recovery, budgets, baseline voice and project-first HUD

- **Bridge state/recovery.** OVERSEER now consumes each producer's atomically
  published `current.json` as an authoritative resynchronization point: a late attach,
  a dropped record or a rotated event file no longer leaves a source's coverage
  incomplete forever. Coverage is split into recoverable *current state* and a
  permanent *history gap*, a session the producer no longer lists is retired instead
  of looking like ongoing work, and a sequence gap is reported instead of smoothed
  over. The unread tail of a rotated event file is drained, and a same-size file
  replacement is detected by file identity rather than by length.
- **Liveness is not action evidence.** A heartbeat keeps the source fresh but can
  never renew an action, overwrite an observed category, or hide an unanswered
  approval/input request behind continuing tool activity. An action older than ten
  seconds becomes unknown coverage, an unrecognized `turn/end` outcome is never a
  completion, a late `turn/end` for a closed turn cannot close the current one, and
  producer clock skew is bounded by the collector's receipt time.
- **Two producer defects found by the new fixtures.** An event-file rotation silently
  swallowed the record that triggered it, and the published sequence reused host
  sequence numbers, so every host event the observer deliberately does not export
  looked like a lost record. Both are fixed and covered.
- **Shared source budgets.** One aggregate per-pass read allowance and one tracked-item
  allowance are divided between enabled sources instead of being multiplied by them;
  at most eight source instances are monitored, truncation is reported, and source
  discovery is bounded per root, deterministic, honours explicitly configured roots
  and still excludes disabled sources.
- **Correct freshness and capability aggregation.** Source freshness is the producer's
  own evidence time, so repolling an idle spool never looks like a fresh read and a
  fresh peer cannot refresh a dead source. Capability sets are merged across readable
  sources with a per-harness note, and a bridge declares what it actually provides
  instead of inheriting the Codex capability set.
- **A failed Claude bridge append is no longer a silent loss.** The bridge counted it
  with a name missing from the health allowlist, so the counter was discarded and the
  record vanished with neither a landed line nor a drop — the intermittent "9
  published records, found 8" the previous review saw. The name is allowlisted, a
  failed append consumes no per-session sequence number, and the concurrency invariant
  now counts every `dropped*` counter instead of lock timeouts alone.
- **Generated producer fixtures.** `integrations/deepseek-harness/fixtures/spool/`
  holds live, rotated and drop-affected spools produced by the real observer state
  machine and the real spool writer; the .NET integration tests consume them, so a
  change in the producer's actual output shape fails the suite.
- **Offline baseline voice is real and selectable.** `Light & voice…` now offers the
  three providers explicitly (voice pack, offline Windows speech baseline, TTS
  endpoint) with the baseline's own availability line, an explicit **Use baseline**
  action and no audio or network request merely from opening the panel. A **new** data
  directory selects the baseline by default (still muted); an **existing** config is
  never reset, including a file that has no `provider` key. Exactly one provider is
  used: no automatic cross-provider fallback, an assigned recording still wins, and a
  failing selected provider stays silent and is reported.
- **Bounded, cancellable speech workers.** SAPI synthesis runs on at most two STA
  workers; further calls report `speech-busy` instead of piling up threads. Every call
  observes cancellation and its deadline *while waiting*, and each worker deletes the
  temporary WAV it created in its own cleanup, so a hung or abandoned engine cannot
  leak files or hold a slot forever. Failures and timeouts are never cached, the
  nine-script cache is bounded, and a missing English voice is visibly unavailable.
- **Project-first HUD.** Projects are now real expandable rows (name, harness badges,
  working/needs-you counts, hour strip, chevron) above their tasks, ordered
  attention → active → recent with stable identity tie-breaks. The last-hour strip
  carries category, evidence, coverage, interval and detail into its tooltip and
  automation name, draws unknown as a hatch that can never be confused with idle, and
  is explained by an eight-entry legend. The competing global count chart is gone from
  the default view, and each project's bins are computed once per refresh instead of
  once per task.
- **Header, menu and sizing completed.** The top-level menu is the agreed short set,
  the visible ✕ hides to tray whenever a tray can restore the window, the header stays
  pinned while content scrolls, branding collapses before the controls, and the size
  matrix (compact, 320×220, 320×720, 440×620, 640×260, 900×700, maximized) renders
  without clipping. Navigation is harness-qualified: an unsupported harness offers its
  task id instead of advertising a route that cannot work, and `Projects & sources…`
  lists discovered sources and project buckets with additive persistence.

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
  install and uninstall helpers. Contract-tested (21 Node tests) but **not**
  live-qualified — Claude Code is not installed on this machine.
- `integrations/deepseek-harness/`: a small host-native observer plugin that exports
  allowlisted status metadata from the user's existing `dsh` host. Tested in
  isolation; not yet attached to a running host, and OVERSEER does not consume the
  spool yet.

- The Codex pipeline retains each session's normalized workspace root and resolves
  its project at admission (`AgentTask.WorkspaceRoot` / `AssignedProject`), with an
  additive `project` config section for root mappings, path aliases and cache bounds.
- `TaskActivityRecorder` records producer-side activity intervals (working,
  thinking, planning, waiting, unknown, plus completion/error markers) from
  normalized snapshots; a terminal marker closes the action timeline without erasing
  the confirmed work.

### Remaining qualifications

- The dsh observer is **not attached to a live host**: that needs the documented
  opt-in plugin install into the user's own dsh profile and a real model-backed
  session. Structural compatibility with the installed `dsh 0.1.5-rc.3` seam
  declarations is verified; live attach is not claimed.
- The Claude Code **CLI** is not installed, so the bridge remains contract-tested with
  synthetic hook payloads only. The Claude desktop app (2.9939.2.0, MSIX, "Cowork") is
  installed and running and bundles a Hooks surface with the same nine event names, but
  it exposes no hooks section in any of its configuration files, and the documented
  Claude Code payload fields are absent from its bundle, so its payload is not verified
  to match the bridge contract. Live qualification needs the CLI or a hook registered
  through the app's own Hooks settings.
- The packaged **HUD lifecycle** block of the release smoke was not executed in this
  round because the user's own running OVERSEER owns the single-instance guard;
  stopping it was not authorized. The rest of the packaged smoke ran.
- A stored source enable/disable decision applies the next time OVERSEER starts, and
  the settings window says so. Details and evidence: `docs/IMPLEMENTATION_STATE.md`.

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
