# OVERSEER Implementation State

## Current
Plan: Post-V1 — Project-centered multi-harness HUD (plan reviewed)
Goal: One integrated implementation/review round for project grouping, activity strips,
responsive HUD controls, and an offline baseline voice
Branch: ux/first-use
Product base: d869db6 (custom HUD implementation: 5521980)

## Current Plan Status
- Astra handoff: reviewed and corrected in ignored `.agent/HANDOFF.md`
- DeepSeek implementation: **partial** — foundation merged on `ux/first-use`; the
  project-first UI, composite multi-source monitor and baseline voice are **not**
  implemented yet. Details in "Project-centered round — execution" below.
- Build / tests: Release build 0 warnings / 0 errors; **483 tests passed, 0 failed**
  (456 reviewed baseline + 27 new for project identity, harness-qualified keys and
  observed activity history)
- Smoke / package: not rerun in this partial round (see the section below for the
  exact gap)
- Astra review: pending implementation completion
- Prior UX baseline: Release build passed with 0 warnings/errors; 456 tests passed;
  26 render fixtures; package smoke 17/17 with HUD lifecycle skipped in favor of
  recorded manual checks. User confirmed Win+Left/Right and Win+Z work.

Confirmed scope: Codex, official DeepSeek Harness (`dsh`), and Claude Code; visible
close hides to tray by default with explicit Exit available; offline English system
voice with custom recordings preserved. The reviewed plan specifies project-root identity,
external metadata-only harness bridges, bounded per-task/project activity intervals,
and shared monitoring budgets. Unknown activity remains distinct from blank idle.

Planning inspected official dsh source at
`46a7f68b0922371ce7144b668b90e377d8e799f4`; installed-version compatibility and live
integration are execution gates, not yet demonstrated. Its compressed session logs
motivate an observer plugin instead of importing its storage stack. Claude one-shot
hooks provide event-driven evidence, not continuous heartbeat or complete history.

Plan review (2026-09-24): corrected dsh session-created exception isolation, per-session
event ordering and silent replay hydration, bounded concurrent bridge publication,
partial-coverage project timelines, explicit voice-provider fallback behavior, and
commit-before-publish ordering. Cross-checked pinned dsh callbacks, local voice/config
and close behavior, script parameters, and current branch/head. No remaining plan
blocker identified; actual installed-version and live harness qualification remain gates.
Documentation diff check passed; no product build/test run is claimed for this review.

Next action: instruct DeepSeek to execute the final prompt in `.agent/HANDOFF.md`
as one complete implementation goal, then conduct Astra review. No product code, running
application, desktop shortcut, or live harness configuration changed during planning.
The frozen original four-plan V1 scope documents remain unchanged; this is an explicit
user-requested post-V1 extension.

## Project-centered round — execution (2026-09-24, partial)

Scope executed by DeepSeek on `ux/first-use` (product base `d869db6`). This is an
honest partial delivery of the reviewed packet: the identity, resolution and activity
foundation landed with tests, and two user-facing behaviours changed; the UI
restructuring and the harness bridges inside the desktop app did not.

### Commits
- `4e99ea0` feat: hide the HUD to tray on close and restore the same session
- `d4ac577` feat: add project identity, harness-qualified keys and observed activity history

### Delivered (implemented and covered by tests)
- **Project identity and resolution** (`Core/Models/ProjectIdentity.cs`,
  `Core/Monitoring/ProjectResolver.cs`, `Core/Configuration/ProjectSettings.cs`):
  opaque hashed `ProjectId`, display name and local root; precedence explicit →
  longest configured root → locally verified Git root → normalized workspace root →
  Unassigned. Complete-segment comparison (so `C:/work/app2` is never inside
  `C:/work/app`), duplicate leaf folders stay distinct, case/separator/dot-segment
  normalization, linked worktrees grouped through `gitdir`/`commondir` verified
  metadata with a checkout label, independent clones separate, WSL/POSIX roots in a
  separate namespace joined only through an explicit alias, device/relative/missing
  paths never resolve against the process working directory, bounded ancestor checks
  (default 32) and a bounded resolution cache. `Describe()` emits name + hash only,
  never a root (asserted).
- **Harness-qualified task identity** (`Core/Models/AgentTask.cs`): `TaskKey`
  carries `HarnessId` and treats `ProfileId` as the stable source-instance id;
  `HarnessIds` fixes `codex`/`dsh`/`claude` and display names. Two-argument
  construction keeps the Codex default (deterministic migration) and equal
  conversation ids in different harnesses no longer compare equal (asserted).
- **Internal adapter contract** (`Core/Monitoring/IAgentMonitorAdapter.cs`): stable
  source identity, immutable `SourceSnapshot` with per-source health, reasons,
  coverage, tasks, transitions and capabilities. Compiled-in, not a runtime loader.
- **Bounded observed activity history** (`Core/History/TaskActivityHistory.cs`):
  producer-side intervals with confirmed/inferred/unknown separation, a bounded
  ten-second belief window (the last action is never extended indefinitely), explicit
  Unknown coverage tails instead of idle, explicit-only idle, completion/error markers
  that do not erase the underlying interval, per-interval project attribution so a
  reassignment affects future intervals only, project union computed by one-second
  segmentation (overlap, never a sum, attention outranks tool work, brief attention
  survives), rolling 60-minute clipping, capacity bounds
  (512 histories / 2048 per task / 100k total) and bounded 720-bin geometry.
- **Close-to-tray and restore** (`App/HudApplicationController.cs`,
  `App/Desktop/ShowRequestSignal.cs`, `App.xaml.cs`): ×/Alt+F4/system-menu/WM_CLOSE
  hide the window while a tray exists; explicit Exit, session ending (logoff/shutdown
  is never cancelled) and the no-tray case keep the unconditional orderly shutdown;
  a second normal launch signals the running instance to restore the same window and
  session through one local named event (bounded waiter thread, no payload).

### New tests (27)
- `ProjectResolverTests` (12): segment boundaries, duplicate leaves, normalization,
  relative/device rejection, Git root precedence, worktree grouping and label,
  clone separation, mapping/explicit precedence, POSIX namespace and alias join,
  bounded ancestor checks and cache, no path in the diagnostic summary.
- `TaskActivityHistoryTests` (15): interval extension, split at the observed instant,
  unknown tail rather than idle, idle only when observed, inferred evidence and
  markers, union overlap vs sum, attention priority, stale member partial coverage,
  window clipping without backfill, capacity eviction, reassignment attribution,
  geometry bounds and brief attention retention, harness-qualified key separation.

### NOT implemented in this round (explicit gaps)
1. **Project-first HUD**: no project rows, harness badges, project/task strips,
   project settings surface or Unassigned row. The existing Now/Recent/Session task
   list is unchanged. No render evidence was produced for this round.
2. **Composite monitor**: `CompositeMonitorSource` and the desktop-side bridge
   readers/adapters do not exist, so the running HUD still observes Codex only.
   `SourceSnapshot`/`IAgentMonitorAdapter` and the bridge envelope are defined but
   unused by composition.
3. **dsh observer and Claude hook bridges**: external integration assets are
   delivered under `integrations/` with provenance, per-asset READMEs and isolated
   tests, but **no desktop consumption yet**. Verified by the implementation role:
   `integrations/deepseek-harness` → `node --test` 29 tests / 29 pass;
   `integrations/claude-code` → `node --test` 19 tests / 19 pass (Windows, node
   24.15.0). Neither is live-qualified: Claude Code is **not installed** on this
   machine (no `claude` on PATH and no `~/.claude`), so no real hook ever fired,
   and the dsh observer was never attached inside a running host (no
   `dsh plugin add` was performed, by design — the user's profile is untouched).
   Installed for reference: `codex` 0.155.1, `dsh` 0.1.5-rc.3
   (`DSH_HOME=%USERPROFILE%\.dsh`), node 24.15.0.
4. **Offline baseline voice**: no `WindowsSpeechVoiceProvider`, no SAPI stream
   synthesis, no baseline selection control, no voice cache. Voice behaviour is
   unchanged from the reviewed baseline.
5. **Workspace root retention**: the parser still reduces `session_meta.cwd` to
   `WorkspaceLeaf`, so no task carries a resolvable project root yet. The resolver is
   complete and tested, but it is not yet fed by the Codex pipeline.
6. **Responsive sizing/header/menu refinement** and the close-button tooltip were not
   touched; the reviewed custom chrome and existing sizing behaviour are unchanged.
7. **Package/smoke for this round**: **not executed**. The last recorded package
   evidence remains the prior UX round's 17 scoped checks from source `5521980`.
   `scripts/Test-PackagedRelease.ps1` was updated to the new close semantics
   (close hides to tray; a second launch must restore; the script terminates only the
   process it started, and no-tray close is still expected to exit), but the updated
   script has **not been run**, because the round stopped before the publish step and
   no clean-source package was produced. Treat the packaged smoke result for this
   branch as unknown until it is executed.

### Known limitations introduced or carried
- `ProjectResolver` resolves from evidence only; the Codex pipeline does not yet
  supply a workspace root, so nothing is grouped in the running product.
- Physical checks (mixed DPI, Snap/Win+Arrow, human tray interaction, audible voice)
  were not performed in this round.
- `SuspendDropsQueuedCuesAndCountsThemWithoutMuting`, flagged in the packet's known
  test risk, passed on every full-suite run in this round (three consecutive runs,
  483/483 each). The fake-player cancellation race is still not deterministically
  characterized; that remains open.

### Next steps for completion
1. Retain `session_meta.cwd` as a normalized workspace root through
   `SessionIdentityReader` → `TrackedSessionFile` → `AgentStateReducer` →
   `AgentTask`, then assign projects with `ProjectResolver` in the monitor.
2. Implement `CompositeMonitorSource` over `IAgentMonitorAdapter` with shared
   budgets, source-local health/freshness and single-evaluation global completion;
   wrap `CodexMonitor` and consume the `integrations/` spools for dsh and Claude.
3. Project-first view model + XAML (`ProjectRowViewModel`, strips from
   `TaskActivityHistory`/`ActivityTimelineGeometry`), header/menu refinement.
4. `WindowsSpeechVoiceProvider` with the injectable SAPI engine boundary, cache
   bounds, precedence rules and the "Use baseline" control.
5. Render/inspect both themes across the packet's size matrix, full tests, clean
   publish and packaged smoke, then Astra review.

## Archived P4 Status
Plan: P4 — Hardening and Release
Goal: G4 — Harden, package, smoke-test, and release V1
Branch: impl/p4-release

## Plan Status
- Astra handoff: originally completed in planning; at Review 4 the local ignored file
  contained only `pp`. Review used the recorded handoff and frozen spec.
- DeepSeek implementation: complete, with Astra Review 4 fixes
- Build: passed, Release, 0 warnings / 0 errors, SDK 10.0.401
- Tests: passed, 398 / 398, 0 failed/skipped
- Smoke: passed, 28 / 28 checks from the final corrected ZIP
- Astra review: PASS WITH NONBLOCKING NOTES (Review 4, 2026-09-22 local date)

## Completed

Review covers the complete P4 branch against reviewed P3/main
`663caf57caf0212699a6d96533a34f1171611c27`. Main is unchanged. Four projects,
frozen documents, original reference PNGs and dependency boundaries are preserved.

### Commits

| Commit | Scope |
| --- | --- |
| `0eaac1d` | P4 planning documents |
| `31e21aa`, `ecce812`, `e4ad382`, `3c9ffc9` | Initial lifecycle/DPI/pack hardening and regressions |
| `9608164`, `25bbfeb`, `dcd70b1` | Original example pack, release scripts, instructions/notices |
| `d5d93c5`, `d7e72ef`, `cb9feec` | Initial smoke corrections, path mapping and implementation evidence |
| `34a4d16` | Review fixes for suspend, late failure, pack validation, package provenance and smoke reliability |
| `6c0fbfb` | Remove unsupported DPI query; finer foreground/input-timing evidence |

### Review findings resolved

- **MAJOR — suspend race.** One-time cancellation allowed a later producer
  snapshot to enqueue audio while suspended. A deterministic regression reproduced
  one queued cue where zero was required. The coordinator now keeps a suspended
  state, observes/latches silently, discards pending work, resumes explicitly and
  filters already-observed pre-resume events. Saved mute is unchanged. Fresh
  post-resume episodes still enqueue normally.
- **MINOR — late provider failure.** A null result or exception after cancellation
  could replace the suspended status with a failure. Generation checks now reject
  those stale outcomes; both cases have regression tests.
- **MAJOR — false-positive packaged asset check.** The checker inspected only
  the first clip for each event. A missing second permission variant still
  returned success. It now validates every manifest variant through the real
  provider. The regression failed before the fix and passes afterward; a negative
  check against the extracted executable also returned exit 1 / `clip-missing`.
- **MAJOR — smoke evidence isolation.** Only extraction was unique; data and
  renders could survive earlier runs, malformed state could pass, and tray
  shutdown accepted nonzero exits. Every run now has fresh data/render directories,
  validates state fields and a nondefault saved theme, requires zero exit codes,
  and cleans up only its owned processes even on script exceptions. Extraction
  deliberately uses a space/Unicode path. Reduced-duration/skipped checks are
  explicitly recorded; foreground samples include input timing.
- **MINOR — release provenance/reliability.** Notices came from the SDK rather
  than the exact redistributed runtime packages. The ZIP now includes matching
  .NET and Windows Desktop licenses/notices; all four license files were compared
  byte-for-byte to their sources. SDK selection uses the repository's global.json
  even from another working directory, NuGet cache paths are XML-escaped,
  archive names cannot escape the output root, prior ZIPs survive until replacement
  validation, and a SHA-256 sidecar is written. Scripts explicitly require PS 7.
- **MINOR — DPI API misuse/dead code.** The new GetDpiForMonitor call ran on a
  per-monitor-aware thread, contrary to Microsoft's API guidance. It was removed;
  the existing actual-HWND GetDpiForWindow and WPF DpiChanged/re-clamp path remains
  authoritative. The pre-HWND system fallback no longer claims each monitor's
  actual scale. Mixed-DPI/negative-origin/removed-monitor policy tests still pass.
  Reference: https://learn.microsoft.com/en-us/windows/win32/api/shellscalingapi/nf-shellscalingapi-getdpiformonitor

### Verification

- Initial submitted branch: Release build 0 warnings/errors, **394/394** tests.
  Two new regressions reproduced the suspend and missing-variant bugs before fixes.
- Final source: `dotnet build TheOverseer.sln -c Release --no-restore
  --disable-build-servers` passed with 0 warnings/errors; `dotnet test
  TheOverseer.sln -c Release --no-build --no-restore`: **398 passed**, none skipped.
- Final self-contained publish restored successfully and ran from outside the
  repository, using committed source `6c0fbfb122c12606aed4c20ea8598d276de4a4b6`
  with `sourceTreeDirty=false`, version 1.0.0, win-x64, no trimming/AOT/single-file.
- Corrected candidate: `artifacts/review4-final/OVERSEER-v1-win-x64.zip`,
  76,379,935 bytes, 491 entries, SHA-256
  `5525E3015027C7EBF4B3B140BF6C409893609165429F0CED91C988FD4122A545`.
  Both included frameworks are 10.0.12. The manifest and `.sha256` sit alongside it.
  Earlier archives under `artifacts/p4-release` and `artifacts/review4-release`
  are superseded review inputs, not the final candidate.
- Final smoke directory:
  `artifacts/review4-final/smoke/run-20260923T062126Z-ffe87484/`.
  Runs use the extracted apphost, unrelated CWD, SDK paths removed, separate fresh
  data for each mode and a path containing spaces/Unicode. Runtime module inspection
  on both corrected candidates confirmed hostfxr/hostpolicy/coreclr loaded
  from the extraction. No settings, logs or agent documents were in the ZIP.
- Six UI renders inspected on the first corrected candidate: Light/Dark shared
  geometry, collapsed/expanded charts, partial/gap treatment, healthy empty and
  unavailable counts. All six final candidate renders are byte-identical to the
  inspected renders.
- First corrected candidate: 27/28 smoke checks passed; the HUD appeared in the
  foreground during a sample. User interaction could not be confirmed or excluded.
  That result is retained under `artifacts/review4-release/smoke/`; it is not
  relabeled as a pass. The final retest passed with 547 samples / zero owned-window
  foreground observations over 60 seconds; input timing is retained. The earlier
  failure was not reproduced, and no unsupported claim about its cause is made.
- Final package: **28/28 checks passed**, all child modes/lifecycle exits 0.
  Finite HUD, silent voice/owned-loopback TTS, all shipped pack variants, synthetic
  active-task monitor, 60-second real monitor, 60-second visible HUD, second
  instance, persisted light theme/mute/valid placement, hidden tray startup and
  visible no-tray fallback passed. No skip/unavailable override was used.
- No additional upstream source, NuGet dependency or framework was introduced.
  Original synthetic cues are opt-in; upstream attribution remains present.

## Known Limitations

- Physical sleep/wake, monitor removal and moving between different-DPI monitors
  remain unverified on this one-display 125% environment. Deterministic lifecycle
  and geometry regressions pass; these are still physical release-qualification
  items, not a claim of hardware validation.
- No clean-machine run without a global runtime; local runtime module loading and
  sanitized child environment are the available self-contained-package evidence.
- Acoustic playback and human tray clicks were not rechecked. Voice smoke uses
  an owned loopback endpoint and synthetic audio; native playback/cancellation
  evidence from P3 remains API evidence, not a claim of hearing the cues.
- Real approval/input remains unsupported by installed Codex rollouts. No guessing
  was introduced; synthetic attention contracts remain covered.
- The final archive records `6c0fbfb`; this later evidence-only ledger commit is
  intentionally not the binary source commit. Byte-identical ZIPs are not promised.

## Blocking Issues
- None for the reviewed code/package. Physical qualification remains explicitly
  incomplete; this verdict does not claim the unperformed checks passed.

## Next Action
- Review 4 is complete. Integration and `v1.0.0` tagging are separate workflow
  actions; neither was performed. Retain the physical sleep/wake, display/DPI,
  acoustic and clean-machine qualifications until actually exercised. Use the
  corrected archive under `artifacts/review4-final/`, not an earlier candidate.

## First-use follow-up — 2026-09-22

User-directed usability changes on `ux/first-use`, based on reviewed P4 head
`551429a`; the four original plans remain complete. Product name stays OVERSEER.

- Visible HUD participates in Windows taskbar/Alt+Tab while automatic updates
  remain nonactivating.
- Default Now view keeps current work/attention and ten minutes of terminal
  results. Recent supports 1/2/4/8/24 hours; This session uses activity since app
  launch. Preferences persist. Filtering never changes monitor counts/state.
- Text, symbol and color status distinguishes work, attention, error and unknown
  monitoring. Unsupported attention shows `?` or a known lower bound, with a
  visible disclosure. Clicking collapsed status opens Now.
- Release build passed with 0 warnings/errors; 405 tests passed, none skipped.
  Tests cover time boundaries/expiry, live-task retention, unknown attention,
  settings persistence through controller shutdown and WPF tab bindings.
- Ten synthetic Light/Dark renders generated under `artifacts/first-use-ui`;
  Now/Recent/Session, collapsed and unavailable layouts inspected.
- Implementation commit: `cdd52b1`; clean-source self-contained package at
  `artifacts/first-use-release/OVERSEER-v1-win-x64.zip`, SHA-256
  `624AF905F8881B102977B17B6D90692E446FE9803D6163AABFCF130A436CBEC7`.
- Full packaged checks: **28/28 passed**, default durations, ten nonempty renders,
  real/synthetic monitoring, voice, second instance, persistence and tray fallback.
  The 60-second live check recorded 547 samples with no owned foreground window.
  Evidence: `artifacts/first-use-release/smoke/run-20260923T065420Z-91e273db/`.
- Replaced the previously launched HUD with a fresh extraction at
  `artifacts/overseer-first-use-cdd52b1/`, retaining the normal user's data/settings.
  Native verification found a visible OVERSEER window with APPWINDOW set and
  TOOLWINDOW clear; `artifacts/first-use-release/live-launch-evidence.json` records
  the launch. The updated app was left running for user evaluation.
- Frozen documents, references, monitoring/voice policies and dependencies are
  unchanged. All physical qualification and real attention-observability limits
  above still apply. No merge or tag. Next: evaluate these controls in daily use;
  the latest first-use candidate supersedes the earlier candidate for this trial.

### Upper-section feedback — 2026-09-23

- User requested broad upper-section dragging, larger red/green status, always-on-top
  behavior, and clarification of "status incomplete / partial".
- Summary and expanded status areas now capture drag gestures; mute/expand keep
  their clicks and the status still opens Now on a click rather than a drag.
- Larger status text uses red for confirmed attention, green for work and amber
  for uncertain activity. Unread history/stale updates receive plain-language
  explanations. Unknown/partial counts remain honest; monitor rules are unchanged.
- The existing live HWND was verified topmost (`WS_EX_TOPMOST` set) and its saved
  preference was already true. That behavior/preferences are retained.
- Release build: 0 warnings/errors; 407 tests passed, including routed input and
  incomplete-zero regressions. Ten updated renders generated; Light/Dark attention
  and unavailable layouts inspected under `artifacts/upper-section-ui` and the
  packaged renders directory.
- Clean source `8967274`; self-contained candidate:
  `artifacts/upper-section-release/OVERSEER-v1-win-x64.zip`, SHA-256
  `419497A988D1BE5E6533B8EA10E3F8A5D827270EAD5C49290C2B763CC8EA09C9`.
- Scoped package checks passed: ten renders and normal startup, visible native
  window, topmost and taskbar flags set, 60 foreground samples over 15 seconds
  with none owned by OVERSEER. Evidence: `artifacts/upper-section-release/live-evidence.json`.
  The prior 28-check package run was not repeated for these UI-only changes.
- Updated app left running from `artifacts/overseer-upper-8967274/`, with existing
  user settings preserved. Human evaluation of the new drag surface is next.

### Intermittent counts, graph and elapsed timer — 2026-09-23

- Root cause reproduced: `ImageView` items and `world_state` records were treated
  as unknown schema. The resulting evidence-gap flag excluded active tasks from
  counts until a later lifecycle event. Four startup/live regressions failed before
  the fix and pass afterward. ImageView now projects bounded ReadingFiles activity;
  world-state context data is explicitly ignored without examining its contents.
  Genuinely unknown schema still degrades counts (negative regression retained).
- Evidence: bounded structural-only inspection of recent local rollout tails,
  plus local upstream `codex-protocol-reference/protocol.rs` WorldStateItem definition.
  No raw conversation data added to the repository. Redacted 20-second before/after
  traces live under `artifacts/monitor-before.txt` and `artifacts/monitor-after.txt`.
  The after trace observed 2 working tasks; stale/history limits remained. Runs
  occurred at different times, so the controlled regressions establish causality.
- Graph now names the metric (working tasks), count scale and hour axis; removed
  the unexplained collapsed miniature. Move cursors mark drag surfaces; buttons
  and header context menu retain interaction-appropriate cursors.
- Active-row elapsed time uses current-turn StartedAt, never LastActivityAt.
  Unobserved starts show a dash. New turns clear prior start/completion timestamps;
  tests cover action changes, a new turn and missing lifecycle evidence.
- Added user-facing CHANGELOG.md and included it in release payload checks.
- Release build: 0 warnings/errors; **414/414** tests passed. Light/Dark chart and
  collapsed renders inspected under `artifacts/monitor-chart-ui`.
- Clean source `ceb2b50`; package `artifacts/activity-fix-release/OVERSEER-v1-win-x64.zip`
  includes CHANGELOG.md, SHA-256
  `D51A9083525549BB2E25881FCCF5AC832B1E1952C30B7AD7211FF77B36194AE9`.
- All **28/28 packaged checks passed**, default durations, no overrides. The
  isolated 60-second normal-HUD check sampled 548 times with no owned foreground
  window. Evidence: `artifacts/activity-fix-release/smoke/run-20260923T072544Z-0659d796/`.
- Updated normal HUD left running from `artifacts/overseer-activity-ceb2b50/` with
  saved user settings. Native window checks confirmed visible/topmost/taskbar.
  This interactive relaunch separately recorded 11 owned-foreground observations
  in 60 samples; user interaction was not attributed, so that observation is not
  claimed as a no-focus pass. See `artifacts/activity-fix-release/live-evidence.json`.
- Branch remains `ux/first-use`; frozen sources and monitoring/privacy/resource
  bounds preserved. Latest candidate is the activity-fix package above.

### Caption and row color refinement — 2026-09-23

- User requested a much quieter, better-scaling graph caption and consistent
  per-task coloring. Caption uses the metadata font size, regular weight and
  muted color in a shrink-only Viewbox. Rows reuse the overall status palette
  for a thin accent and status line; stale/provisional takes amber precedence.
- Release build: 0 warnings/errors; 414/414 tests passed. Ten fixture renders
  generated under `artifacts/caption-rows-ui`; dark Now and light Recent inspected.
- CHANGELOG.md updated. Clean source `a6b12ca`; candidate:
  `artifacts/caption-rows-release/OVERSEER-v1-win-x64.zip`, SHA-256
  `16B5FDF2356AC60CE8BE91CBEE6EBE625300A4B99D1F774EC2826B823C670E9C`.
- Scoped package check passed: ten renders; updated normal HUD visible, topmost,
  taskbar-enabled and no owned foreground window in 60 samples over 15 seconds.
  Evidence: `artifacts/caption-rows-release/live-evidence.json`. Existing settings
  preserved; app left running from `artifacts/overseer-caption-a6b12ca/`.
- No monitor or lifecycle behavior changed; the preceding full 28-check package
  result remains separate from this scoped visual-update check.

### App icon and concise status copy — 2026-09-23

- User-supplied artwork preserved byte-for-byte in `design/branding/overseer-source.png`
  (SHA-256 `7F6EBFF07B6E4B8D5E39CCC5F660C4F0032C4EF8C5A12170613AB1AA7EB87F4C`).
  Mechanical Windows icon conversion preserves transparency and includes 16, 24,
  32, 48, 64, 128 and 256 pixel frames. Embedded for executable, window and tray;
  tray owns and disposes its icon independently.
- Visible statuses use Idle, Limited visibility and Monitor offline. Replaced
  persistent diagnostic paragraphs with one compact coverage/input-detection
  label; full details remain in tooltips and the header's Monitoring details menu.
  Unknown/lower-bound counts and monitoring semantics are unchanged.
- CHANGELOG.md and README.md updated. Release build: 0 warnings/errors;
  **415/415 tests passed**, including embedded icon frame/native tray-size decoding.
- Clean source `b99fc68`; candidate `artifacts/identity-copy-release/OVERSEER-v1-win-x64.zip`,
  SHA-256 `28D51021297F742712333742E554A181235CD8043CF6372EC45FBB3706A839AF`.
- Scoped package checks passed: ten fixture renders; Light/Dark expanded and
  unavailable layouts inspected. Extracted executable icon and live window icon
  visually match the supplied artwork. Native window remained visible, topmost,
  taskbar-enabled, with zero owned-foreground observations across 60 samples/15s.
  Evidence: `artifacts/identity-copy-release/live-evidence.json`, `icon-evidence.json`
  and PNGs. The full 28-check smoke suite was not repeated for this icon/copy update.
- Updated normal app left running from `artifacts/overseer-identity-b99fc68/` with
  saved settings. Branch remains `ux/first-use`; no merge/tag or dependency changes.

### Audible notification check — 2026-09-23

- Ran the packaged `b99fc68` executable with `--voice-smoke --audible` and an
  isolated data directory. PASS: 4 pack and 5 loopback/fallback playback API
  successes, no device-unavailable result; duplicate suppression, re-arm,
  completion aggregation, mute/no-backlog and TTS failure fallback checks passed.
  Native in-flight cancellation completed in 11ms.
- Evidence: `artifacts/voice-audible-20260923/stdout.txt`. Synthetic tones only;
  this does not verify speech quality or real Codex approval/input observability.
  User confirmed the tones were heard clearly. Normal HUD settings remain muted;
  its selected `example` pack was not installed and no TTS endpoint was configured.

### User-authorized light and voice setup — 2026-09-23

- Additive follow-up on `ux/first-use`, base `118787b`; no new numbered plan and no
  edits to the frozen specification. User explicitly approved the larger optional
  status light and in-app nine-event recording setup, followed by review.
- Light & voice panel is reachable from header and tray menus. Steady strip has
  four positions and 6–24 DIP thickness; default off, settings persisted. Same
  status palette and layout in both themes. WAV assignment copies validated audio
  locally; per-event toggles, status, scripts, preview and stop are provided.
- Four new lifecycle events supplement the original five. Loss waits ten seconds;
  restore requires a successfully played loss cue. Startup and one five-minute
  reminder are opt-in. Reminders require fresh explicit request evidence, never
  inactivity or historical discovery. UI actions and cancellation stay silent.
- Review fixed cancellation being eligible as error audio, sleep after terminal
  failure, disabled all-complete suppressing individual completion, wrapper fallback
  diagnostics and provider/settings lock ordering. Pending work is discarded on
  preview, mute, configuration changes and suspend; reconnect snapshots cannot
  replay task transitions. Imported clips remain local and bounded; no dependencies.
- Final review: **PASS WITH NONBLOCKING NOTES**. No unresolved blocker/major/minor
  findings in this follow-up. Real Codex approval/input observability remains
  limited; user speech recordings have not been supplied. Physical device/display
  qualifications recorded under Plan 4 remain separate from automated checks.
- Release build: 0 warnings/errors; **438/438 tests passed** (23 added), including
  lifecycle timing/relevance, no-backlog behavior, cancellation, settings persistence,
  recording bounds/provider precedence and all four strip positions with DPI rounding.
  Sixteen fixture renders generated; Light/Dark settings and strip layouts inspected.
- Clean source `a536bc7`; candidate `artifacts/light-voice-release/OVERSEER-v1-win-x64.zip`,
  SHA-256 `22F3F09BA0CC73E3C84743171158403388001F5A333EC91E025909AE1DB57C3B`.
  **28/28 packaged smoke checks passed**, default durations and no skips. Nine
  example event mappings validated; 548 foreground samples over the isolated
  60-second normal-HUD check found no owned foreground window. Evidence:
  `artifacts/light-voice-release/smoke/run-20260923T085512Z-4d29dddf/`.
- Relaunched normal app from `artifacts/overseer-light-voice-a536bc7/`, preserving
  existing settings. Native checks: visible, topmost, taskbar-enabled, no owned
  foreground window in 60 samples/15s. Evidence: `artifacts/light-voice-release/live-evidence.json`.
  Light remains optional/off by default; notifications remain muted unless enabled.
  CHANGELOG updated, branch clean after evidence commit; no merge/tag.

### Direct light controls and resizing — 2026-09-23

- User requested a surface-menu option, larger width, right-side default and
  direct dragging to resize. Added Status light directly to the HUD menu with
  position and width presets. Default is right/32 DIPs, adjustable from 6 to 64.
- The colored surface is a resize grip with horizontal/vertical cursor matching
  its side. Existing debounced settings persistence saves changes. Four routed
  drag tests cover direction and bounds; expansion expectation includes strip width.
- Release build: 0 warnings/errors; **442/442 tests passed**. Default expanded
  width is 392 DIPs (360 content + 32 light). Sixteen packaged renders generated;
  default right-side expanded layout inspected with no overlap.
- Clean source `cd2b89c`; candidate `artifacts/light-resize-release/OVERSEER-v1-win-x64.zip`,
  SHA-256 `A72CCA97CE4255BFD312331E4ADF085BF472B630C5406AFBF5AEE7B19AA783B7`.
- Scoped native check: visible/topmost/taskbar, zero owned-foreground observations
  in 60 samples/15s. Evidence: `artifacts/light-resize-release/live-evidence.json`.
  Preceding full 28-check run retained separately; not repeated for this UI update.
- App left running from `artifacts/overseer-light-resize-cd2b89c/`. Applied the
  user's requested right/32 preference after gracefully closing the previous
  instance; other saved settings preserved. Changelog updated, no merge/tag.

### Visible HUD light toggle — 2026-09-23

- Clarified request: toggle must be directly on the HUD, not just in a menu.
  Added LIGHT ON / LIGHT OFF beside mute, with active color and action tooltip.
  Toggling preserves width and restores the preceding side within the session.
- Doubled maximum thickness to 128 DIPs in configuration, drag grip, slider and
  menu presets. Default remains right/32. Changelog and README updated.
- Release build: 0 warnings/errors; 442 tests passed. Sixteen packaged fixture
  renders generated; visible header toggle inspected in collapsed Dark and expanded
  Light layouts. Scoped native check passed: visible/topmost/taskbar, no owned
  foreground observations in 60 samples/15s. The previous full smoke remains separate.
- Source `d44bbdc`; package `artifacts/light-toggle-release/OVERSEER-v1-win-x64.zip`,
  SHA-256 `EB2F59C67EE06CC2E4D1739F2941A32F85FB2D520A4CB8FB87C2EB7CC0EE8B1B`.
  Evidence: `artifacts/light-toggle-release/live-evidence.json`. Updated app left
  running from `artifacts/overseer-light-toggle-d44bbdc/`, existing settings preserved.

### Replacement emblem — 2026-09-23

- Selected the user's white standalone emblem (`01_44_11 AM.png`) for app,
  window/taskbar and tray icons. Source retained byte-for-byte in
  `design/branding/overseer-source.png`; SHA-256
  `3FFC955D05D39444FB4C6E020A85A34D6D39152A2E7901A599E930FF5CF7B7FA`.
- Mechanically generated the seven existing Windows icon sizes, preserving
  transparency and artwork. Inspected the 32px result; no product logic changes.
- Release build passed with zero warnings/errors; all 442 tests passed, including
  embedded icon frame and tray decoding checks. Changelog updated.
- Packaged source `3405a26`; archive SHA-256
  `00903EBB67206A12508E0CDF15AA3F1E051B541AAC0B7F6D00D70D5A7017C2F9`.
  Sixteen fixture renders generated; executable icon extracted and inspected.
  Scoped live check confirms visible/topmost/taskbar; 11/60 foreground samples
  occurred during the interactive session, so this run does not establish absence
  of focus activation. No focus code changed; full smoke was not repeated.
- Updated and verified `C:\Users\tester\Desktop\OVERSEER.lnk` target and icon.
  App left running from `artifacts/overseer-emblem-3405a26/`, settings preserved.
  Evidence in `artifacts/emblem-release/`; no merge/tag.

### Thin outlined emblem — 2026-09-23

- Added the requested thin black outline using built-in imagegen. Original source
  remains untouched; edited asset is `design/branding/overseer-outlined.png`.
  Prompt: add only a very thin black outline around constituent shapes (about
  4px at 1280px), preserve geometry/spacing and white-gray fills, transparent
  background and negative space, no text/shadow/redesign. Generated result also
  reduces the original edge speckling. Converted to the existing seven ICO sizes.
- Release build: zero warnings/errors; 442 tests passed. Packaged source `1e675ca`;
  archive SHA-256 `AFA759C97C6FE08F295070A93692F534712E7851B238BE5615AA4AA16BE10D25`.
  Sixteen fixture renders; extracted executable icon inspected. Scoped live check:
  visible/topmost/taskbar, zero foreground observations in 60 samples/15 seconds.
  Shortcut target/icon verified; updated app running with existing settings.
  Evidence: `artifacts/outlined-release/`. Full smoke not repeated for asset edit.

### Header controls cursor and drag — 2026-09-23

- Entire upper-right controls area now forces the normal arrow cursor, including
  gaps and health dot, and is excluded from header drag capture. Surrounding
  summary remains draggable; popup menu also forces the arrow cursor.
- Extended routed-input regression to cover the controls panel and health dot.
  Changelog updated; no monitoring or voice behavior changes.
- Release build: zero warnings/errors; 442 tests passed. Packaged source `1351dda`;
  archive SHA-256 `E97799079740BB13BC4DCB05C4C741D6B1A80789CF86444ACC1AD2238C25A5F0`.
  Sixteen packaged fixture renders; scoped native check confirms visible/topmost/
  taskbar and zero foreground observations in 60 samples/15 seconds. Existing
  settings preserved, app restarted and desktop shortcut updated. Evidence in
  `artifacts/header-controls-release/`; full package smoke not repeated.

### Window scaling implementation — 2026-09-23

- User approved `WINDOW_SCALING_PLAN.md`. Added explicit HUD / Window view via
  header arrow and menu. Same window/controller/monitor/voice session; native
  caption and resize controls in Window view, original borderless HUD otherwise.
  Disabled transparent HWND creation (shell still paints the existing background)
  so native chrome can be enabled without recreating the monitoring session.
- Separate additive persisted Window-view bounds, maximize state and pin setting;
  legacy version-1 state retains HUD placement. Windows owns live resizing/Snap;
  light, model and expansion changes cannot reset Window-view geometry. Native
  WINDOWPLACEMENT retains normal bounds while maximized. Monitor recovery clamps
  only when outside the usable work area or when the saved monitor is absent.
- Taller layouts expose more tasks, narrow headers omit redundant branding,
  short layouts scroll, graph points scale without thickening strokes. Light is
  constrained to leave usable content space without changing its stored setting.
  Light & voice settings can shrink/scroll with Stop/Close always accessible.
- Automated tests: 453 passed, including scale-policy cases 100/125/150/175/200/
  250/300 percent, invalid/legacy state, view round trips, removed monitor and
  live-update bounds preservation. These simulate geometry; they do not change
  the machine's display scale. Twenty-six fixtures include both themes and five
  Window-view sizes with maximum light width; narrow/tall/short renders inspected.
- Native probe `scripts/Test-WindowScaling.ps1` passed at actual DPI 120 (125%):
  resize styles, maximize hit target, resize retention, maximize/restore and normal
  bounds persistence. Evidence: `artifacts/window-native-3/`. Earlier probe attempts
  exposed probe startup timing and PowerShell DPI virtualization, corrected in the
  test harness; final checks use PerMonitorV2 coordinates.
- Limitations: native Snap keyboard/flyout interactions, unplug/reconnect and
  sleep/resume still need hands-on qualification. Windows
  text-size-only accessibility scaling is not wired to the explicit font resources;
  Windows display scaling works through WPF. No separate app zoom was introduced.
  No merge/tag.
- Review: PASS WITH NONBLOCKING NOTES for the documented manual qualifications.
  Found and fixed a clipped voice mute control by wrapping the settings heading
  row; final build/tests passed (453 tests). Final 26 renders inspected, including
  corrected settings. The fixed native caption avoids custom hit-test code.
- Full default-duration package smoke: 28/28 passed for `395a06d`, including
  547 foreground samples with no owned foreground window. Evidence:
  `artifacts/window-scaling-release/smoke/run-20260924T025356Z-8810dd42/`.
  Subsequent source `db60aa6` changes only the settings heading WrapPanel; final
  package received fresh renders, scoped launch and native-window verification.
- Final archive `artifacts/window-scaling-final/OVERSEER-v1-win-x64.zip`, SHA-256
  `61C2B40FE8921C2A937C3D2BEC4417867398024C11554AF008D87F9A42B8133A`.
  Desktop shortcut updated; app running from `artifacts/overseer-scaling-db60aa6/`
  with saved user settings preserved. Scoped live check: visible/topmost/taskbar,
  0/60 foreground samples. Evidence: `artifacts/window-scaling-final/`.
- Final isolated native test also moved across the actual primary 125%-scale and
  left portrait 100%-scale displays and back, preserving logical width (608 DIPs)
  and work-area reachability. Resize, maximize/restore, native maximize hit target
  and persisted normal bounds all passed on the final package. Evidence:
  `artifacts/window-scaling-final/native-monitor-check/window-scaling-evidence.json`.

### Own-HUD window chrome — planning revision

- User clarified that resizing/Snap should retain OVERSEER's own HUD rather than
  switch to a stock Windows title bar. Replaced WINDOW_SCALING_PLAN.md with the
  next implementation plan: custom chrome, native behavior underneath, explicit
  drag/control/light/edge input boundaries, and real custom-button Snap validation.
- Planning only. Existing app, package and shortcut remain unchanged. Implementation
  awaits user instruction; the previously tested sizing/monitor foundation is retained.

### Own-HUD window chrome — implementation and review

- Implemented after explicit user approval. WPF WindowChrome retains the custom
  header in compact, expanded, resized and maximized states. A small owned HWND
  adapter provides native caption hits and the custom maximize/Snap target.
  No dependency, monitoring, voice-policy or frozen-document changes.
- Input boundaries: outer six DIPs resize; header background moves; controls,
  menu and light remain client interactions. Maximize uses one pointer owner,
  cancels on release outside/capture loss, and remains keyboard accessible.
  Native gestures defer compact promotion until completion, preserving previous
  larger bounds on cancellation. Menu shows the current pin preference.
- Build: Release, zero warnings/errors. Tests: 456/456 passed, including new
  hit-region, command-routing and compact resize commit/cancel cases. Initial
  test invocation hit an expired fixture's file lock; cleaned up that owned test
  process. One initial run failed the existing timing-sensitive voice suspend
  test; the subsequent complete suite passed. No failure hidden or test removed.
- Rendered 26 fixtures; inspected compact, narrow/tall and wide/short layouts in
  both themes. Evidence: artifacts/custom-chrome-renders/.
- Real interactive checks on this machine: custom button pointer maximize/restore,
  release outside cancels, Tab/Space activation, Alt+Space system-menu maximize,
  drag from maximized to normal, outer-edge resize and separate HUD menu passed.
  Actual Windows Snap hover flyout observed. User confirmed both Win+Left/Right
  and Win+Z work on the isolated custom-chrome build. These are new observations,
  not inherited stock-title-bar evidence.
- Review: PASS WITH NONBLOCKING NOTES. Mixed-DPI policies remain tested; physical
  mixed-DPI transitions were qualified on the preceding sizing foundation and
  were not repeated on this header revision. Unplug/sleep/resume and text-size-only
  accessibility remain the previously disclosed qualifications. No merge/tag.
- Packaged source: 5521980. Self-contained archive:
  artifacts/custom-chrome-release/OVERSEER-v1-win-x64.zip; SHA-256
  659790C1778F2D85D33DBEA5683A18FA4289C1A8370E623007ECE98F71BD4D66.
  Scoped packaged smoke: 17/17 passed (26 renders, finite HUD/voice/pack checks,
  synthetic and live monitoring). Used -SkipHudLifecycle because interactive
  validation used Computer Use instead of the PowerShell window-driving block;
  this is not a fresh 28-check lifecycle/focus qualification. Evidence:
  artifacts/custom-chrome-release/smoke/run-20260924T035007Z-8a3b6752/.
  Desktop shortcut now targets artifacts/overseer-chrome-5521980/; launched with
  existing user settings. Shortcut receipt is in artifacts/custom-chrome-release/.
