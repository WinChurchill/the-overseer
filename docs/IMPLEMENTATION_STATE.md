# OVERSEER Implementation State

## Current
Plan: P3 — Activity and Voice
Goal: G3 — Activity trace and useful non-annoying voice notifications
Branch: impl/p3-history-voice

## Plan Status
- Astra handoff: ready (.agent/HANDOFF.md, whole Plan 3)
- DeepSeek implementation: complete, with Astra review fixes
- Build: passed (0 warnings, 0 errors, .NET SDK 10.0.401)
- Tests: passed (375 passed, 0 failed, 0 skipped)
- Smoke: passed (silent/native voice, loopback TTS, HUD, real monitor, live lifecycle)
- Astra review: PASS WITH NONBLOCKING NOTES (Review 3, 2026-09-21)

## Completed

Review covered the entire P3 branch against reviewed P2/main
`a5d851bcceb58ad904d1357b4e9a9a006ea57d1a`. Main remains unchanged. Four projects,
dependencies, frozen documents, reference PNGs and upstream attribution remain
unchanged; monitoring semantics and Plan 4 packaging were not expanded.

| Commit | Scope |
| --- | --- |
| `9093ebe` | Whole-plan handoff preparation |
| `fddcb4a` | Core history, voice policy/manager/contracts/settings/validation |
| `b22787b` | Sparkline and voice projection/control |
| `af3fb08` | Pre-pump integration, pack/TTS/WAV adapters, diagnostics |
| `80f4f32` | P3 tests |
| `c5b0fdb` | Initial implementation documentation |
| `fddb057` | Astra Review 3 fixes and regression coverage |

### Review findings resolved

- **MAJOR — notification semantics.** Later hydration and state-only changes could
  initiate speech; repeated events with new timestamps bypassed active latches.
  Only explicit fresh events now initiate cues, with current condition/turn checks,
  once-per-episode latching, stale-snapshot rejection and bounded replay guards.
- **MAJOR — relevance and mute.** Production playback had no meaningful
  revalidation. Resolved cooldown alerts, old-turn completions and obsolete groups
  could play; muted aggregation survived unmute. Candidates retain group members,
  are rechecked before provider work, fallback and playback, and mute atomically
  discards all pending buffers while retaining silent episode guards. Global
  completion requires complete, fresh coverage and matching completed turns.
- **MAJOR — cancellation/lifetime/queue.** A cancelled SoundPlayer start could
  run after Stop; late provider results could reach playback during disposal;
  shutdown waited for disk before stopping voice. Native asynchronous WAV start
  and Stop now share a lock and cancellation fence. Shutdown fences immediately,
  joins repeated disposal, and disposes owned providers after the worker exits.
  Queue wakeups are capped and lower-priority completions cannot evict attention.
- **MAJOR — pack/WAV boundaries.** Ancestor junctions escaped containment,
  malformed version types threw, and inconsistent RIFF/rate/alignment fields
  bypassed validation. Complete paths are checked for reparse points, Windows
  traversal/device aliases are rejected, reads fail silently with codes, and PCM
  chunk sizes/rate/alignment/duration are cross-checked without overflow.
- **MAJOR — history/UI.** Downsampling could bridge unknown samples or >10-second
  gaps, stored buckets did not expire without input, and collapsed mode omitted
  the required chart. Unsafe display columns now break the trace; stopped history
  trims; monitor failure retains past observations with an unobserved tail.
  Both modes show the sparkline, using one shared layout for Light/Dark.
- **MINOR — presentation/evidence.** Theme changes now invalidate cached chart
  pens, later provider failures remain visible, and fixtures use independent
  visual trees with explicit empty history. Smoke fixtures now use matching task
  and event turns, production relevance checks, and native in-flight cancellation.

History remains memory-only: 60 minutes, at most 3,601 one-second buckets and 240
display columns. Columns containing discontinuities may omit nearby observations
instead of inventing continuity. Voice remains muted by default, queue 32,
10-second per-kind/task cooldown, 1-second start spacing, 2-second aggregation,
30-second candidate age, 4,096 replay entries and 1,536 latches for up to 512 tasks.
Future timestamps beyond the 5-second clock-skew allowance are rejected.
Provider/pack/endpoint edits load at restart; HUD mute applies immediately.

### Verification evidence

- Initial 349 tests passed; new targeted regressions reproduced 13 failures before
  fixes. Final suite: **375/375**, including all 220 reviewed P1/P2 cases and 26
  additional review cases. Junction fixtures use real temporary Windows junctions.
- `dotnet build --no-restore --disable-build-servers`: **0 warnings/errors**.
  `dotnet test --no-build --no-restore`: **375 passed, 0 failed/skipped**.
- `--voice-smoke`: **exit 0**. Once-only cues, re-arm, aggregation, mute/unmute,
  five owned-loopback TTS requests, exact short payload, HTTP 500, timeout, wrong
  MIME and corrupt audio with one local fallback each.
- `--voice-smoke --audible`: **exit 0**. Nine synthetic clips accepted by the real
  WAV API, no device failure; an additional two-second tone cancelled in **7 ms**.
  Neither audibility nor acoustic stop was verified by ear.
- `--monitor-debug --duration 60 --redact-labels`: **exit 0**, monitoring readable,
  unsupported attention observability still disclosed.
- `--hud-smoke --duration 15`: **exit 0**, tray/placement/persistence and clean
  shutdown; 280x72 DIP at 125% DPI.
- Normal live HUD **60 seconds**: visible, **0 foreground changes / 0 HUD foreground
  samples**, second launch exited 0 without disturbing the first. Hidden startup
  remained hidden; `--no-tray` fallback remained visible. All owned processes closed
  with exit 0 after repeated close requests.
- Six final renders inspected: Light/Dark x collapsed/expanded plus healthy-empty
  and unavailable. Chart colors, partial/gap treatment, mute, empty baseline and
  unknown counts checked. Artifacts are ignored under `artifacts/review3-*`;
  final images are in `artifacts/review3-final-ui`.
- `git diff --check` passed. No new dependency, frozen-source change or upstream
  source reuse. Existing cached restore assets used; no machine paths added to build.

## Known Limitations
- No supplied speech pack or external TTS endpoint: synthetic PCM tones and an
  owned loopback server prove mechanics/contracts, not speech quality or a service.
- Real approval/input events remain unsupported by the installed Codex rollout
  format; those attention contracts are verified synthetically, not inferred.
- Physical sleep/wake, secondary monitors, mixed DPI and human tray interactions
  remain unverified. Lifecycle checks used one display at 125% DPI; tray/control
  paths are covered through fixtures and controller tests.
- Live window checks establish focus/lifetime, not an acoustic observation or a
  busy live-history workload; changing chart data is covered synthetically.

## Blocking Issues
- None. All actionable Review 3 findings were fixed and re-reviewed on P3.

## Next Action
- Integrate the reviewed P3 branch as a separate workflow step, then Astra prepares
  the whole Plan 4 / Goal 4 handoff. This review does not merge, push or tag.
