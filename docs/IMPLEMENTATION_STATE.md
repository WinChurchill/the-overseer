# OVERSEER Implementation State

## Current
Plan: P3 — Activity and Voice
Goal: G3 — Activity trace and useful non-annoying voice notifications
Branch: impl/p3-history-voice

## Plan Status
- Astra handoff: ready (.agent/HANDOFF.md, whole Plan 3)
- DeepSeek implementation: pending
- Build: pending for P3; reviewed baseline passes with 0 warnings/errors
- Tests: pending for P3; fresh baseline 220 passed, 0 failed/skipped
- Smoke: pending for P3
- Astra review: pending (Review 3); Review 2 passed with nonblocking notes

## Planning State
- Reviewed P2 commit a5d851bcceb58ad904d1357b4e9a9a006ea57d1a was fast-forwarded
  into main; impl/p3-history-voice was created from that clean base.
- Fresh baseline build/tests rerun during planning: 0 warnings/errors, 220 passed.
- The P3 handoff defines the whole history/voice goal, exact code map, ordered work,
  anti-spam/cancellation rules, bounded providers, tests and direct execution prompt.
- No P3 product implementation has begun. No endpoint or spoken assets supplied;
  fake/loopback TTS and synthetic WAV fixtures are planned, not live speech evidence.

## Reviewed Foundation (P1/P2)
Review 2 base: reviewed P1 commit `7c665609c36d8d86c2ff6d09b5aacf596c3937ed`
(main before P2 integration).
Planning ledger: `e8ee951`. Implementation commits: `9759e85` (Core settings and
placement), `d436f14` (UI), `75f9620` (App), `276e24b` (tests), `9269536` (docs).
Review fixes are in the subsequent `fix: address Astra Review 2 findings` commit.

P2 adds the persistent WPF HUD, one shared Light/Dark window, normalized task rows,
health and capability disclosure, bounded placement/state, tray and header menus,
opt-in HKCU startup, safe Desktop task navigation, and isolated diagnostics.
Collapsed geometry is 280×72 DIP; expanded is up to 360×392 DIP with a bounded
scrolling task list. Core remains free of WPF/WinForms. No runtime package, project,
P3/P4 feature, frozen-document change or reference-image change was introduced.

## Astra Review 2
Reviewed the full P2 branch against main, the frozen specification (§9/16),
AGENTS.md, the P2 handoff, source/tests, and runtime evidence. User authorization
allowed immediate fixes on this branch. No architecture escalation was needed.

Fixed findings:
- MAJOR — settings and placement saves blocked the Dispatcher; settings changes
  could queue writes. Startup file loading now runs in a worker; one debounced
  background writer captures immutable UI settings, coalesces pending changes,
  flushes at exit and reports failures. Controller logging uses a bounded 32-entry
  background queue; disk/log waits at exit are bounded.
- MAJOR — hidden startup plus unavailable/disabled tray stranded the HUD.
  Fallback now shows the window, refuses unrecoverable hiding, and provides a
  header context menu including settings and Exit. Partial tray creation cleans
  up allocated resources.
- MAJOR — monitor termination could leave healthy details/rows or accept late
  snapshots. All terminal outcomes now become unavailable with unknown counts;
  stale rows are removed, task launches stop, exceptions are observed without
  logging private text, and SnapshotPump.Stop is terminal. Synthetic/fake sources
  now obey the lifetime-task contract. The old fault test asserted initial state
  without dispatching the fault; it now proves actual failure delivery.
- MAJOR — concurrent exits could bypass cleanup. Exit is guarded, shutdown callers
  share completion, disposal is idempotent, and session ending invokes cleanup
  without cancelling Windows logout. Logs/tray/window/monitor are released.
- MAJOR — task-id copy was documented but had no UI wiring. Explicit row context
  menu action now works; a WPF test opens that menu and invokes the real handler.
  Missing protocol registration is checked before requesting shell navigation.
- MAJOR — drag state was only captured after debounce, permitting snap-back on
  quick expansion; DPI changes had no recovery event. Drag now captures position
  immediately, DPI changes schedule recovery, show reapplies bounded geometry,
  and fallback screen enumeration puts the actual primary monitor first.
- MINOR — startup smoke could report success after failed enable/disable and did
  not restore in finally or preserve registry value type. It now verifies all
  transitions and restores the original unexpanded value and type in finally.
  Disabling startup also reads back actual state.
- MINOR — corrected canonical NEEDS_INPUT/NEEDS_APPROVAL text, added full health
  tooltip, constrained large summaries to fit, and corrected stale README claims.

## Validation
Commands used with the workspace .NET 10 SDK and cached packages:
- `dotnet build --no-restore --disable-build-servers`: pass, 0 warnings/errors.
- `dotnet test --no-build --no-restore`: 220 passed, 0 failed/skipped.
- New regressions cover terminal monitor delivery/late snapshots, hidden-start
  fallback, real copy-menu wiring, Dispatcher responsiveness and coalescing under
  a blocked writer, shared shutdown completion, and shutdown with a stalled disk.
- `--hud-fixture`: six synthetic views rendered; light/dark collapsed/expanded
  plus healthy-empty/unavailable inspected under ignored `artifacts/review2-ui`.
- `--hud-smoke --duration 14`: exit 0, tray available, theme/opacity persisted,
  clean shutdown; 125% scale maps 280×72 DIP to 350×90 physical pixels.
- Real normal HUD: 60 seconds, visible, second launch exit 0 while first remained
  running; repeated WM_CLOSE requests exit 0 with shutdown-complete log.
- Hidden-start and hidden-start + `--no-tray`: respectively hidden and visible,
  both exit 0. Follow-up 15-second normal + 4-second hidden/fallback checks recorded
  zero OVERSEER foreground samples and zero foreground changes.
- `--monitor-debug --duration 60 --redact-labels`: exit 0, readable lifecycle and
  structured activity, explicit unsupported approval/input observability.
- `--hud-startup-check`: Enabled → verified Enabled → Disabled; original restored.
- `git diff --check`: pass. main remains unchanged; no merge, tag or push.

Evidence is local and ignored under `artifacts/review2-*`; no real rollout data or
screenshots of private task titles were committed. Previous P2 evidence remains
available under `artifacts/p2-*`. No new restore was necessary.

## Known Limitations / Nonblocking Notes
- Approval/input events remain unsupported in this Codex version; synthetic
  contracts do not establish live visibility. Zero needs-you is not proof that
  no request exists; capability disclosure remains available in the HUD.
- No registered `codex:` handler on this machine. Exact task focus is unverified;
  navigation uses an honest manual/copy fallback and never executes a task.
- Mixed-DPI secondary displays, physical monitor removal and sleep/wake were not
  exercised. Placement policy tests and recovery handlers exist; this review's
  real display was 125% DPI. Real Windows logout was not triggered.
- Tray callbacks and window/context-menu handlers were tested; actual shell tray
  icon/menu mouse interaction was not independently exercised in this review.
- A stalled disk can lose the latest preferences when bounded shutdown expires;
  it cannot queue unbounded writes or keep the HUD process alive indefinitely.
- History, sparkline, mute and voice remain Plan 3; packaging remains Plan 4.

## Blocking Issues
- None remaining from Review 2.

## Next Action
DeepSeek reads AGENTS.md, .agent/HANDOFF.md and this ledger, then executes the
whole Plan 3 / Goal 3 on impl/p3-history-voice. Build/test/smoke, update this ledger,
and leave the branch clean and unmerged for Astra Review 3.
