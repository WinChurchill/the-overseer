# OVERSEER Implementation State

## Current
Plan: P4 — Hardening and Release
Goal: G4 — Harden, package, smoke-test, and release V1
Branch: impl/p4-release

## Plan Status
- Astra handoff: complete (.agent/HANDOFF.md, whole Plan 4 / Goal 4)
- DeepSeek implementation: complete on `impl/p4-release` (9 commits after the
  documentation-only plan head `0eaac1d`)
- Build: Release build passed, 0 warnings / 0 errors (.NET SDK 10.0.401)
- Tests: 394 passed, 0 failed, 0 skipped (reviewed 375-case P1–P3 baseline + 19 new)
- Smoke: packaged smoke passed 28/28 checks from a fresh extraction of the final ZIP
- Astra review: pending (Review 4)

## Completed

### Implementation commits (base: reviewed P3 `663caf5`)
| Commit | Change |
| --- | --- |
| `31e21aa` | Suspend discards stale voice inline; late cancellations cannot relabel state |
| `ecce812` | Placement resolves per-monitor DPI (`GetDpiForMonitor`, system-DPI fallback) |
| `e4ad382` | Missing clips report `clip-missing`; new silent `--voice-pack-check` mode |
| `3c9ffc9` | Regressions for watcher failure, hint overflow and optional source recovery |
| `9608164` | Original opt-in example voice pack under `assets/voicepacks/example/` |
| `25bbfeb` | `scripts/Publish-Release.ps1`, `scripts/Test-PackagedRelease.ps1`, version 1.0.0 |
| `dcd70b1` | README release/example-pack instructions, notices, provenance |
| `d5d93c5` | Packaged smoke checks made payload-relative and timing-real |
| `d7e72ef` | Checkout path mapped out of shipped binaries; leak audit; state check |

### Hardening (spec §18)
- **Sleep/wake**: `PowerModes.Suspend` is handled inline on the events thread
  (a posted callback would not run before sleep): pending cues are discarded, the
  in-flight provider call and audio are cancelled/fenced, the generation is
  bumped, and the **saved mute preference is untouched**. `Resume` re-runs
  geometry repair and reconcile on the UI thread. Latch/dedupe state survives, so
  a condition that is still active after wake is not announced twice, while a new
  episode still speaks. Late cancellations can no longer overwrite the recorded
  reason code.
- **Mixed DPI / multi-monitor**: screen enumeration asks the shell for the DPI of
  the monitor that owns each screen centre (`GetDpiForMonitor`, MDT_EFFECTIVE_DPI)
  and falls back to the system DPI only when that query is unavailable; the
  per-window DPI still governs the real placement conversion. Placement
  regressions cover two screens at different scales, a huge offset clamped inside
  the owning work area, negative origins and a removed high-DPI screen.
- **Restart during an active task**: a restarted process hydrates the running task
  silently (no cue, no provider call) and speaks only the later explicit event.
- **Watcher**: a sessions root that did not exist at construction still recovers
  through bounded polling (reported as `watcher-failed`, never a healthy zero);
  a 400-file burst proves hint overflow is reported and the hint set stays capped
  at the configured bound.
- **Optional state DB**: unavailable/locked/schema-mismatched results keep the
  JSONL-derived counts authoritative and recover in-process without a restart; the
  real read-only path also ran live (60 s monitor-debug, exit 0).
- **Packs/providers**: a missing clip file is typed `clip-missing` instead of the
  conservative `clip-reparse`; missing/malformed/removed pack cases are covered;
  a 50-event completion burst yields exactly one aggregate with bounded membership
  (≤ 32) instead of a burst of utterances; `--voice-pack-check` validates the
  *shipped* pack through the production provider (voice smoke generates its own
  pack, so it cannot prove packaged assets).
- Existing suites were extended rather than replaced; the 26 Review 3 regressions
  remain in place.

### Release package
- `artifacts/p4-release/OVERSEER-v1-win-x64.zip` — 76,380,582 bytes, 490 entries,
  SHA-256 `F77C067E82EBFAE8BEBE2473BABDA9D9D18522EE56CA1815350295E9961907B5`.
- Built by `scripts/Publish-Release.ps1` from committed source `d7e72ef` (clean
  tree, no `-AllowDirtyTree`): self-contained Release/win-x64 **folder** publish,
  `TheOverseer.App.exe`, `Version=1.0.0`, `PublishSingleFile=false`,
  `PublishTrimmed=false`, `PublishAot=false`; included frameworks
  `Microsoft.NETCore.App 10.0.12` and `Microsoft.WindowsDesktop.App 10.0.12`.
- Payload = publish output + `README.md` + `THIRD_PARTY_NOTICES.md` +
  `licenses/` (codex-monitor-hud MIT, .NET `LICENSE.txt`/`ThirdPartyNotices.txt`
  from the publishing runtime) + `examples/voicepacks/example/`. No repository
  files, agent documents, tests, caches, ZIPs, logs or private data.
- `-p:PathMap=<repo>=/_/` keeps the build checkout path out of the shipped
  binaries and symbols; the script fails the build when any payload file embeds
  it. Independent scan of the final archive: 484 content files checked, 0 hits.
- `build-manifest.json` records source commit/branch/dirty flag, version,
  configuration, SDK, included frameworks, payload file list with sizes, archive
  size and SHA-256. It contains no machine-specific cache paths or secrets.
- Runtime packs were restored from nuget.org through a scoped, ignored NuGet
  config generated by the script (no machine path is committed).

### Packaged smoke evidence (`artifacts/p4-release/smoke-final2/`)
Fresh extraction of the final ZIP, extracted apphost run from an unrelated
working directory, one isolated data directory per mode, and the child
environment stripped of `DOTNET_ROOT`, `DOTNET_CLI_HOME`, `NUGET_PACKAGES`,
`MSBuildSDKsPath` and every PATH entry containing `dotnet` (including
`C:\Program Files\dotnet\`), verified with 0 leaks.

| Check | Result |
| --- | --- |
| Archive required entries / apphost / FileVersion 1.0.0 / self-contained frameworks | pass (490 entries, 1.0.0.0, both frameworks) |
| No private payload, sanitized child environment | pass (0 offending paths, 0 env leaks) |
| `--hud-fixture` | exit 0, 6 non-empty renders |
| `--hud-smoke --duration 15` | exit 0 |
| `--voice-smoke` | exit 0, owned loopback endpoint |
| `--voice-pack-check` on the shipped pack | exit 0, 5/5 events validated |
| `--monitor-debug` synthetic home (15 s) | exit 0, 1 tracked file, `#1 … WORKING`, 1 working |
| `--monitor-debug` real local Codex home (60 s, redacted) | exit 0 (readable) |
| Normal HUD, 60 s | alive, visible window, 30 foreground samples with the HUD never foreground |
| Second instance | exit 0 while the first stayed alive |
| Graceful close of the owned window | exit 0; `state.json` (dpi 1.25, monitor recorded) + `config.json` (theme dark, muted true) persisted |
| `--start-in-tray` hidden start | alive, no visible window, never foreground, clean close |
| `--start-in-tray --no-tray` | visible, controllable window, clean close |

### Spec §25 V1 acceptance accounting
| Item | State | Evidence |
| --- | --- | --- |
| Packaged build launches directly | tested | extracted apphost, unrelated CWD, no SDK/runtime on PATH |
| Active tasks detected; duplicates deduplicated | tested | deterministic synthetic run + real read-only run; identity/dedupe suites |
| Normalized state, current-action labels | tested | reducer/classifier suites, live rows |
| Approval state where observable | unsupported upstream | Codex 0.155.x keeps approval events transient; synthetic contracts only |
| Input state where observable + documented | unsupported upstream | same, documented in README/ledger |
| Monitoring failure visible (never zero) | tested | unavailable/degraded suites, synthetic recovery runs |
| Collapsed + expanded HUD, sparkline, task rows | tested | 6 fixture renders inspected, view-model suites |
| Light/Dark share one layout | tested | fixture renders + theme suites |
| Drag/position persistence | tested (automated) | placement/state suites + packaged `state.json`; human drag not repeated |
| Tray / startup | partially tested | hidden tray start, no-tray fallback and clean close automated; human tray clicks not exercised; Run entry unit-tested and last verified live in P2 |
| Voice packs, mute, anti-spam | tested | pack-check on shipped assets, policy/manager suites, persisted mute |
| Generic TTS endpoint | tested against owned loopback | no external service used by design |
| Sleep/wake | **physical test not performed** | deterministic suspend/resume lifecycle tests + inline discard path |
| DPI/multi-monitor recovery | **physical test not performed** | per-monitor DPI code + mixed-DPI/negative-origin/removed-screen regressions |
| Build/tests pass | tested | Release 0 warnings/0 errors; 394/394 |
| Upstream notices included | tested | archive entry checks + notices file |
| Release ZIP smoke-tested | tested | 28/28 checks from a fresh extraction |

## Known Limitations
- Physical sleep/wake, monitor disconnect and moving the HUD across monitors with
  different DPI were not performed; only one display (125%) is available. The code
  paths and deterministic regressions exist, but the physical checks remain open
  release-qualification items.
- A clean-machine test is not possible here: a global .NET 10 runtime is installed.
  The packaged run removed `dotnet` from the child PATH and loaded the runtime
  from its own folder, which is the strongest available substitute.
- Acoustic output was not heard: the packaged voice smoke is silent by default and
  `--audible` was not run. Native start/stop, cancellation and loopback TTS are
  API-verified only.
- Human tray interaction and the real Run-key registration path were not
  re-exercised by the automated smoke (the script avoids touching user state).
- Real approval/input events remain unobservable in the installed Codex version.
- Byte-identical reproducibility is not claimed: the same source and command
  produce the same file list and content, but archive timestamps (and therefore the
  hash) differ per run. The requirement is a repeatable, validated process.
- The archive embeds source `d7e72ef`; this ledger is a later documentation-only
  commit, so the branch head differs from the binary's embedded commit by design.

## Blocking Issues
- None. The remaining open items are physical/acoustic qualification checks, not
  implementation blockers.

## Next Action
- Astra Review 4 reviews `impl/p4-release` against `main` (whole branch): full
  spec compliance, failure behavior, tests, package contents, dependencies,
  provenance, dead code and release readiness. Do not merge, tag `v1.0.0` or
  publish externally before that review passes.
