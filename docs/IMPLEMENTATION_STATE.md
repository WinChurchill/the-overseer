# OVERSEER Implementation State

## Current
Plan: P3 — Activity and Voice
Goal: G3 — Activity trace and useful non-annoying voice notifications
Branch: impl/p3-history-voice

## Plan Status
- Astra handoff: ready (.agent/HANDOFF.md, whole Plan 3)
- DeepSeek implementation: complete on this branch (see Completed)
- Build: passed (0 warnings, 0 errors)
- Tests: passed (349 passed, 0 failed, 0 skipped: 220 reviewed P1/P2 cases + 129 new)
- Smoke: passed (voice smoke silent + audible, monitor regression, HUD smoke, live HUD 65 s)
- Astra review: pending (Review 3)

## Completed

Base is reviewed P2 commit `a5d851bcceb58ad904d1357b4e9a9a006ea57d1a` (= local main);
planning head was `9093ebe`. Bootstrap and P1/P2 were not rebuilt or redesigned, and
the frozen documents, reference PNGs, four projects and dependency set are unchanged.

| Commit | Scope |
| --- | --- |
| `feat(core)` | `ActivityHistory` + `ActivityChartGeometry`, `VoiceSettings`, voice contracts, `VoiceEventPolicy`, `VoiceManager`, pack/WAV validation, notification formatter, `NotificationCoordinator` |
| `feat(app)` | pre-pump observation wiring, shared mute, pack provider, generic TTS provider, WAV playback, composition/shutdown/settings capture, voice smoke with an owned loopback endpoint |
| `feat(ui)` | `ActivitySparkline`, activity/voice projection, header mute control and the expanded chart/status strip |
| `test` | history, policy, pack/WAV, TTS, manager, coordinator, settings, sparkline and pre-pump delivery coverage |
| docs | README and this ledger |

### Implemented behaviour

- **Pre-pump observation.** `HudApplicationController.OnSnapshot` feeds a short
  serialized `NotificationCoordinator` before `SnapshotPump` coalesces anything,
  so attention raises, clearances and re-raises are never lost to UI coalescing.
  The producer path only records history, evaluates the policy and enqueues
  immutable candidates: no file, network, audio, waiting or Dispatcher work.
- **History.** One-second buckets, 60-minute window, 3,601-bucket cap, newest
  observation wins inside a bucket, strictly older samples ignored, nothing
  persisted. Gaps >10 s and unknown samples break the line; partial counts keep a
  distinct dashed style; the fixed 60-minute axis uses at most 240 columns so
  100k input samples stay bounded. The expanded HUD shows one working-count chart
  with a zero baseline and a quantifier line, and its tooltip discloses unknown,
  partial and gap coverage.
- **Voice policy.** Only `AttentionEvent` initiates notification. Dedupe is by
  kind + task key + turn + observation time + code (bounded 4,096 entries,
  60-minute expiry), aggregate episodes carry their own identity, and events older
  than 30 s or in the future are rejected. Latches speak once per episode, re-arm
  only after fresh observed clearance or a new turn, and treat missing, stale or
  provisional tasks as "no clearance evidence". A pending approval produces
  exactly one cue while a held input latch stays alive without a second cue.
  Cooldown is 10 s per task+kind with ineligible-but-relevant cues deferred up to
  30 s and never retried forever.
- **Completion aggregation.** 2-second window, task+turn dedupe, one grouped cue,
  and a valid all-complete notice replaces the group. Revalidation rejects new
  work, error, cancellation, disappearance, unknown/partial working counts and
  stale evidence, while a fresh per-task completion stays valid under unrelated
  partial coverage.
- **Playback and mute.** One serial consumer, bounded 32-entry priority queue
  (permission → input → error → completion), 1-second start spacing, 30-second TTL,
  and instant mute that clears the queue, cancels in-flight provider/playback work,
  stops the player immediately and fences late results by generation. Unmute and
  restart never replay a backlog. Provider, player, clock and policy are injectable
  contracts; Core stays free of WPF, WinForms, network and audio types.
- **Packs.** Manifest and clip validation (64 KiB manifest, 5 known events, 16
  clips, 4 MiB/15 s per clip, RIFF/WAVE PCM checks, round-robin variants) with
  containment, reparse-point, traversal, UNC and ADS rejection. Missing or invalid
  packs report a code and stay silent instead of crashing.
- **TTS.** Exactly `{"text","event","task"}` with a `Task N` alias, https or
  loopback http only, no redirects, `ResponseHeadersRead` streaming with a 4 MiB
  hard cap regardless of `Content-Length`, a linked 1–15 s timeout and one local
  fallback. No endpoint means no request at all.
- **Lifecycle.** Monitor failure stops voice, clears history and leaves unknown UI.
  Shutdown stops voice before the bounded monitor disposal, persists voice settings
  through the existing single background writer (all monitor/HUD fields preserved),
  and repeated exits join the same cleanup.

### Verification evidence

```text
dotnet build / dotnet test      -> 0 warnings 0 errors; 349 passed / 0 failed / 0 skipped
--voice-smoke (silent)          -> exit 0: pack cue once, duplicate replay silent, verified
                                   re-arm once, completion group = one cue, mute stops
                                   playback, muted/unmute silence, TTS success + 500 +
                                   timeout + wrong MIME + corrupt audio each fall back once
                                   (5 loopback requests, payload fields event/task/text only)
--voice-smoke --audible         -> exit 0: 9 clips sent to the real sound device,
                                   device-unavailable=False; reported as "not verified by ear"
--monitor-debug --duration 60   -> exit 0 (P1 behaviour unchanged, partial counts honest)
--hud-smoke --duration 15       -> exit 0: window/tray/placement/persistence and clean
                                   shutdown retained; 280x72 DIP = 350x90 physical at 125% DPI
live HUD (65 s, expanded)       -> real monitor data with a growing chart, muted voice,
                                   foreground unchanged, graceful close exit 0, placement
                                   recovery disclosed, state.json + voice config persisted
--hud-fixture                   -> six renders inspected (light/dark x collapsed/expanded,
                                   plus healthy-empty and unavailable) with the chart, the
                                   partial style and a real gap visible
```

Sandbox note (unchanged): the workspace sandbox cannot read the user-level NuGet
config, so restore used the ignored `artifacts/nuget-offline.config` with build
servers and node reuse disabled. No repository file depends on it.

### Acceptance criteria A1–A8

- A1 pass: bounded, honest latest-hour history with one working-count chart, finite
  empty/constant rendering, visible unknown/partial/gap treatment and shared themes.
- A2 pass: pre-pump policy preserved raise/clear/raise under a producer flood with
  no UI drain; hydration and degraded replay stay silent; cooldown and re-arm hold.
- A3 pass: 2-second groups produce one cue, all-complete supersedes them, and new
  work, error, cancellation or unknown/partial coverage invalidate the aggregate.
- A4 pass: valid packs play; invalid, missing, escaping and oversized inputs fail
  safely; playback is serial, cancellable and off the UI thread.
- A5 pass: the TTS contract, privacy, size, timeout, cancellation and single local
  fallback are verified against a fake handler and a real loopback server; no
  endpoint means no request.
- A6 pass: mute is visible, persistent, immediate for current audio and late HTTP,
  and unmute/restart never replay a backlog; every existing config field survives.
- A7 pass: all 220 baseline tests still pass, queues/caches/history are bounded, and
  no frozen document or P4 scope changed.
- A8 pass: build/tests/smoke ran for real and four theme/mode renders were inspected;
  limits are recorded below. Synthetic tones prove mechanics, not spoken voice.

## Known Limitations
- No spoken voice pack or TTS endpoint/credentials were supplied. All audible
  evidence is synthetic PCM tones; the audible run was not verified by ear.
- Approval/input attention remains unsupported on this Codex version, so live
  once-per-condition speech for real approvals is untested; the contracts are
  covered synthetically and the capability is still disclosed in the HUD.
- The loopback TTS server proves the HTTP/WAV contract, not any external service
  behaviour (accounts, authentication, provider-specific schemas).
- Sleep/wake, secondary monitors and mixed DPI remain synthetic-only; the live run
  used one 2048x1152 display at 125% scaling.
- The live HUD chart showed a flat zero line because every tracked conversation was
  already DONE; a busy chart was exercised through the synthetic fixture.
- Tray item clicks were exercised through the controller API and the smoke fixture,
  not by a human clicking the icon.

## Blocking Issues
- none

## Next Action
- Astra Review 3 reviews `main...impl/p3-history-voice` with the evidence above.
  Do not merge, tag or push; Plan 4 (release hardening/ZIP) starts only after that
  review passes.
