# OVERSEER Implementation State

## Current
Plan: P4 — Hardening and Release
Goal: G4 — Harden, package, smoke-test, and release V1
Branch: impl/p4-release

## Plan Status
- Astra handoff: originally completed in planning; local ignored file now contains
  only `pp` and was preserved. Review used the recorded handoff and frozen spec.
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
- Build/test and scoped packaged verification are recorded after validation.
