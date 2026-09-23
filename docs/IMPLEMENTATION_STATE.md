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
- Packaging and normal-window validation pending. Frozen documents, references,
  monitoring/voice policies and dependencies unchanged. All physical qualification
  and real attention-observability limits above still apply.
