# OVERSEER Implementation State

## Six-area automated pre-user pass — 2026-09-27 — PASS WITH DEFERRED QUALIFICATION

Added `scripts/Test-PreUserRelease.ps1` to compose existing build/tests, packaged
checks, bridge suites, both upgrade/rollback modes and native scaling into one
sequential run. The report retains six areas and their remaining manual/external
gates. Default visible checks stay on a secondary display; only the intentional
second-launch activation checks defer. No live profiles/settings/shortcut changes.

Expanded packaged integration to cover async partial/full answers and rearming.
Fixed a false-positive degradation predicate that accepted healthy `live` state;
the corrected check requires limited coverage with truncation/stale evidence.
Scaling probe cleanup now terminates only its owned process if orderly exit hangs.
Release build: **0 warnings/errors; 722 .NET tests passed, none failed/skipped**.
Clean private **RC8**, source `f14dc47`, includes the previous async-answer fix.
Archive SHA256: `CB55897A6A75F4B5CBDF975D904DEDCE08347B8BE1F96C7793C726A22461BA93`.
Full six-area run: `artifacts/pre-user/run-20260928T065109Z-aab5b5d0/`, with
`pre-user-report.json` as its index. Results:

- Packaged smoke: **41 passed / 0 failed / 3 deferred** (two intentional activation
  scenarios and the dependent first-instance survival check). Full-duration checks;
  60-second HUD run had 544 foreground samples, zero owned-foreground samples/takeovers.
- Extracted integration **17/17**, real composition with invented sources **42/42**;
  DeepSeek bridge **30/30**, Claude bridge **29/29**. Read-only live Codex monitor passed.
- **46** fixture renders generated; light collapsed and dark expanded spot-checked.
  Native resize/maximize/restore/persistence passed on DISPLAY2 at 96 DPI.
- Real RC7 -> RC8 upgrade AND rollback **25/25 in each HUD/Window mode**, comparing
  89 settings paths per run. Comparison controls **55/55**, mocked placement **3/3**.
- Silent voice smoke passed, including actual offline English synthesis/preview,
  dedupe/cooldown and failure handling; all **9** voice-pack events resolved.
- Subsequent test-runner review fixed empty-evidence handling and made unexpected
  statuses fail closed. **7 report controls passed** separately; future combined
  runs include them. These script-only corrections do not change RC8's app binaries.

Verdict: the automated portions pass; this is not full release qualification.
Still needed: clean Windows/browser download/SmartScreen and another machine/account;
real Claude/dsh host setup, interaction and teardown; correlated Codex approval
integration; physical DPI/Snap/drag/unplug/sleep; tray/logon and audible/device checks.
Run the command in README; opt into `-IncludeActivationChecks` only on an idle desktop.
No live app replacement, user-setting/harness edits, merge or public upload occurred.

## Codex async answer fix — 2026-09-27 — implemented; approval connection blocked

User authorized fixing the live-test findings. Added a bounded parser for the exact
desktop user-reply envelope: only tool-call IDs, question indices and the original
question count reach the reducer. It clears matching async questions without waiting
for turn completion; multiple calls and partial replies remain independently pending.
Acknowledgements, unrelated output, duplicate/old/wrong-turn replies, malformed or
oversized envelopes and unknown question counts cannot dismiss other attention.
Answer references cannot clear permission requests or establish a new task turn.
No prose or command body is retained; no new dependency or frozen-document change.

Verification: Release build with **0 warnings/errors**, **722 tests passed, 0 failed,
0 skipped**. Live read-only monitor + HUD-view-model retest also passed: the new
question raised attention at 06:32:05.834 UTC and the user's actual answer cleared it
to Working at 06:34:19.734 UTC, during the same active turn. Evidence and observer
source: ignored `artifacts/live-attention-fix/`. This is not a rendered/audio check.
Final code review included partial/batched answers, replay/hydration, privacy,
bounded input sizes and approval isolation; no remaining defect found in this fix.

The user confirmed seeing and manually approving the previous timestamp dialog:
the zero permission-request records from that live test are a confirmed limitation,
not a missing human confirmation. **Approval detection is not fixed.** The desktop
app-server processes expose only stdio in the observed configuration, and the existing
daemon's read-only version query could not connect. Read-only schema inspection found
no pending-approval table in the existing state/queue/history stores; this does not
claim no future version can expose one. Official app-server approval requests and
`serverRequest/resolved` provide suitable correlated signals, but no accessible
connection to this desktop session was established. A second independent server
would not observe approvals owned by this process. PermissionRequest hooks alone
lack the matching resolution identity needed to clear concurrent approvals reliably.

Remaining work requires an explicit Codex event bridge/host integration that forwards
request and resolution metadata, including source/session/turn/request identities,
with disconnect handling and restart hydration. It must never answer approvals, infer
them from command text/timing, or clear them on an unrelated tool completion. Do not
claim this source supported until the bridge is installed and qualified live. Official
contracts: https://learn.chatgpt.com/docs/app-server and https://learn.chatgpt.com/docs/hooks.
Diagnostics now describe the rollout-source limit instead of making a universal
claim that no approval events exist. Live app/settings/shortcut and Codex settings
remain unchanged; no package replacement, merge or public upload.

## Live Codex attention test — 2026-09-27 — input clearing requires work

Tested RC7's shipped Core/UI assemblies with a read-only live observer of this chat,
using the actual CodexMonitor and HudViewModel. Installed Codex CLI reports
`0.158.0-alpha.2.1`. No visible HUD, audio, live settings or harness profiles changed.
Evidence: ignored `artifacts/live-attention-rc7/` (observer source, timestamped
state samples, structural metadata only, and `results.json`).

- Async multiple-choice request: **PASS for detection**. At 06:16:55.813 UTC,
  NeedsHuman changed false -> true, capability became LiveObserved, and the HUD
  view-model headline became "Needs you". Unrelated work did not clear it.
- Answer clearing: **FAIL for immediate clearing**. The real answer arrived at
  06:17:50.169 UTC; attention remained `input-requested-nonblocking` afterward,
  including before the approval-test invocation. Current parsing treats the answer
  as user-message activity; the existing turn-boundary fallback was not exercised
  in this finite test. This was previously documented as a limitation, not fixed.
- Approval: request invoked at 06:17:58.501 UTC after the user switched to Ask for
  approval; the harmless timestamp write completed at 06:18:32.071 UTC. The monitor
  recorded zero permission requests, capability remained Unsupported, and no
  structured approval event appeared in that rollout interval. The user subsequently
  confirmed manually approving that dialog. The earlier input alert is
  not evidence of approval detection.

Correction: earlier release text claiming all Codex input requests were unobservable
was stale. Async request detection is live-observed; answer resolution and approval
are separate limitations. The five RC7 corrective findings remain closed, but this
test does not qualify the complete attention experience for public release.

## Astra corrective implementation — 2026-09-27 — PASS WITH NONBLOCKING NOTES

User authorized implementing the five review solutions. They are now implemented:
shared authorized-monitor setup plus actual bounds checks for every composition;
validation before every mutating Claude install; strict persisted-window comparisons;
WPF voice-error notifications; real adapter construction on restart and source-failure/
recovery/removal checks. No new dependency or product redesign.

Release build/test: **704 passed, 0 failed, 0 skipped**. Claude **29/29**, dsh **30/30**;
pure upgrade-comparison negative controls **55/55**; mocked upgrade placement **3/3**.
Real composition on DISPLAY2: **42/42**, including seven placement assertions.
Evidence: `artifacts/astra-fixes/composition-a411b299/run-20260928T045245Z-07d80842/composition-report.json`.
An earlier development run exposed three overly strict new test assertions: recovery
must restore current coverage while retaining observed history gaps. Corrected those
assertions without weakening production history semantics; final run passed.

All five local review findings are closed in source commit `5aba6c2`; final diff
review found no additional blocking defect in these corrections. Both the working
diff and `git diff --check 65a3c15..HEAD` are clean. Branch remains `ux/first-use`.

Clean internal candidate `1.0.0-rc.7+5aba6c2`:
`artifacts/rc7/OVERSEER-1.0.0-rc.7-win-x64.zip`, SHA-256
`88531C43F86071E6F7F4581C03FB30818404D874A484B668C50935FF5D93EC74`.
Manifest records clean source, 568 payload files and 75 integration assets; the
apphost is unsigned. Independently extracted identity is recorded in
`artifacts/rc7/astra-packaged/identity.json`.

Extracted-package verification, with invented producer data and isolated settings:
- Composition **42/42**, including all seven actual window-placement assertions:
  `artifacts/rc7/astra-packaged/composition/run-20260928T045928Z-f563f2a5/composition-report.json`.
- Monitoring-to-UI integration **14/14**:
  `artifacts/rc7/astra-packaged/integration/run-20260928T050003Z-65249048/integration-report.json`.
- Silent voice smoke **PASS**, including real offline synthesis/preview, dedupe,
  mute, no backlog replay and owned loopback failure cases:
  `artifacts/rc7/astra-packaged/voice/stdout.log`. This is not audible qualification.
- Real RC6 -> RC7 upgrade and rollback **25/25 in each mode**, with all 89 JSON
  paths compared strictly except valid save timestamps, five custom WAVs and spool
  documents preserved. HUD evidence:
  `artifacts/rc7/upgrade-hud/run-20260928T045527Z-32c2c0fd/upgrade-evidence-20260928T045527Z.json`.
  Window-view evidence:
  `artifacts/rc7/upgrade-window/run-20260928T045628Z-7f638b8b/upgrade-evidence-20260928T045628Z.json`.

Visible checks used DISPLAY2. Full interactive RC7 lifecycle smoke was not rerun:
its second-launch restore intentionally activates the window. Historical RC6
lifecycle evidence remains separately labeled. Public-release gates and actual
interactive/audio/clean-machine qualification remain open. Live HUD/settings,
shortcut, startup registration and real harness profiles are unchanged. No merge,
push or public upload. The historical review and execution evidence follow below;
their CHANGES REQUIRED verdict and corrective prompt are superseded for these five
findings by this entry.

## Astra review — 2026-09-27 — CHANGES REQUIRED

Reviewed `65a3c15..7ee1c16`: eight commits, 32 files. The branch-wide architecture
and frozen source boundaries remain intact; no significant scope divergence or new
framework was found. Stage-1 cleanup and substantial stage-2 implementation landed,
but the claim that all locally executable work is complete is not yet supported.

Actionable findings (the ordered corrective prompt follows below):

1. **MAJOR / P1 — composition checks can open on the primary display.** Three graphs
   in HudCompositionCheckRunner (lines 134, 184, 292) omit WindowState/placement and
   the shared platform overrides. HudComposition does not consume PlacementMonitor
   itself; the controller shows the window using default primary placement. Passing
   `--placement-monitor` from the package script therefore does not constrain every
   window. This is code-path evidence; the unsafe visible check was not rerun.
2. **MAJOR / P2 — installer writes new hooks to a missing same-path bridge.** After
   a successful one-event install, renaming the fixture bridge away and applying
   another event returns exit 0/applied=true and writes that event's broken hook.
   The `recordPathIsNew` gate skips validation despite a real settings mutation.
3. **MAJOR / P2 — upgrade checks waive placement loss.** Path-only exemptions accept
   invalid dimensions/coordinates, the wrong monitor, lost maximization and even
   deleted fields. Independent helper probes changed six saved-window fields and
   then removed them: both yielded zero failing differences. Current 25/25 evidence
   cannot establish complete saved-window preservation.
4. **MINOR / P2 — muting does not update bound voice failure text/visibility.**
   A real WPF TextBlock binding keeps the failure text after IsMuted=true although
   the computed getter is empty. Dependent PropertyChanged notifications are missing;
   the new test checks only the computed value and misses the rendered behavior.
5. **MAJOR / P2 — normal-composition coverage remains incomplete.** Restart creates
   its own adapters and injects them into ComposeAsync, bypassing the production
   construction the check is meant to protect. Source removal/restart and the
   promised unsupported/unreadable/stale-recovery normal-composition cases are absent.
   These are locally executable gaps, not external-machine blockers.

Independent verification: Release build/test **695 passed, 0 failed, 0 skipped**;
Claude **25/25**, dsh **30/30**, upgrade-placement **3/3**. The first sandboxed build
was denied access to a markup cache; the authorized rerun built and passed. RC6 ZIP
SHA-256 independently matches `2ECD384070FE21F8D4E694250B399C2664CEAFC3A276F5873B58BE05F9BA5931`.
Its manifest names clean product source `9f805c8`; HEAD also changes the smoke script,
so source/script/evidence identities must remain distinct. Existing RC6 44-check smoke
evidence and narrow fixture renders were inspected, not rerun or newly qualified.
`git diff --check 65a3c15..7ee1c16` reports new trailing blank lines in two Markdown
files; a clean unstaged diff alone did not check the implementation range.

Reproductions: ignored `artifacts/astra-stage2-review/` contains installer result,
WPF binding probe and upgrade-comparison probe. Placement evidence:
`artifacts/upgrade-placement-tests/d43122f0e07041a4bb3dcb2967158b96`.
No product fix, live HUD/config/shortcut change or visible launch was made in this
review. External qualification remains open, separately from these local findings.

## Astra review continuation — 2026-09-27 — CHANGES REQUIRED

Act as DeepSeek implementation on ux/first-use. Review base is 65a3c15 and reviewed
HEAD is 7ee1c16; RC6 product source is 9f805c8. This is one corrective continuation
of the combined stage-1 cleanup/stage-2 goal, not a new plan. Read AGENTS.md and
the newest IMPLEMENTATION_STATE entry, then complete the following in order:

1. Fix composition-check placement/isolation before any visible rerun.
   HudCompositionCheckRunner's empty-first-run, no-harness and post-restart graphs
   omit the shared Overrides helper and its WindowState placement seed. Apply the
   same isolated platform services and requested monitor to EVERY graph. Verify
   the prepared state before showing and actual bounds after showing. If the
   requested display is absent/unauthorized, defer before launch, not on primary.
   Add deterministic coverage for all scenarios, no-secondary and invalid targets.
   Keep the user's HUD, live data, startup registry and main display untouched.

2. Close the Claude installer same-path validation hole. Repro: install SessionStart
   to a valid copied bridge, rename bridge.mjs away, then apply SessionStart,Stop
   with the same --bridge path. Current result: exit 0, applied=true, missing path,
   new Stop hook written. Validate every settings-mutating apply, not only a path
   different from the first existing owned handler. Require file validity even for
   any retained --no-verify option; do not exempt missing payloads. Preserve true
   unchanged-run idempotence and unrelated hooks, with no write/backup on refusal.
   Regress same-path missing/broken payload, added event and mixed old hook paths.

3. Make upgrade/rollback placement comparison detect loss. The global path-only
   normalization exemptions classify arbitrary corruption AND missing-after for
   WindowWidth/Height/X/Y/Monitor/Maximized as explained. A probe replacing width/
   height with 1, coordinates with 99999 and monitor with WRONG reports zero failures;
   deleting all six also reports zero failures. Require persisted fields and validate
   expected restored/clamped values with a documented tolerance from the actual test
   display; only accept scenario-proven normalization. Test unchanged topology and
   both HUD/window modes, plus negative mutations/deletions and legitimate clamping.
   Check in the comparator regressions and correct the acceptance claims accordingly.

4. Repair voice-error WPF notifications. IsMuted must raise VoiceFailed and
   VoiceFailureLine when their value changes; ApplyActivity must publish consistent
   final dependent values when detail/mute change together. Bound TextBlock repro:
   after a failure, setting IsMuted=true gives VoiceFailureLine="" but the binding
   still displays the old error. Existing test checks the getter only. Add binding
   or PropertyChanged regression assertions for mute/unmute and simultaneous changes.

5. Finish the promised normal-composition coverage. The restart scenario manually
   constructs adapters and injects them into ComposeAsync, bypassing production
   adapter creation for the newly discovered Claude source. Use real discovery/
   construction on both starts; substitute invented producer files/platform side
   effects only. Add the locally executable remove/restart and missing/unsupported/
   unreadable source plus stale/recovery cases from the packet, asserting applied
   source/task health and healthy-peer survival. Do not mark these missing tests as
   external host limitations. Keep synthetic versus live evidence clearly labeled.

Run full Release build/tests, both bridge suites, placement/comparator regressions
and corrected isolated composition/integration/package/upgrade checks. Visible runs
must remain on an authorized secondary monitor without intentional focus takeover;
defer actual interactive checks if no safe test session exists. Publish a new unused
internal candidate from clean source, not overwrite RC6; record source/hash and which
scripts produced evidence. Update ledger/changelog/release matrix; remove introduced
EOF whitespace reported by git diff --check 65a3c15..HEAD. Report local completion
separately from remaining external gates. No merge, push, public upload, live app
replacement or real harness-setting changes. Return one review-ready continuation.

## DeepSeek implementation — 2026-09-28 — stage-1 cleanup + stage-2 implementation

**Status: local implementation complete for everything executable on this machine; the
branch is review-ready. Public-release qualification is BLOCKED and is not claimed.**

Two outcomes, reported separately as the packet requires:

| Outcome | Verdict |
| --- | --- |
| Local implementation of the combined packet | **complete** |
| Public-release qualification | **blocked** — external environments and owner decisions outstanding |

Branch `ux/first-use`. Planning baseline `171f556`; this round's base `65a3c15`.
Candidate archive built from source `9f805c85b3bc9ef8147923c214b0ec2f4b84643a`; the
documentation commits that follow are not part of the archive payload.

### Commits this round

```text
64ce198 fix: refuse unrunnable bridge payloads and expose the real composition seam
3f51e68 feat: verify normal composition and fix audited UI contract defects
5d3cf35 docs: correct integration install semantics from verified host evidence
c98b13a feat: make lifecycle qualification safe and prove packaged graceful exit
c69d51b refactor: remove demonstrated dead UI code found by the carryover audit
9f805c8 docs: consolidate the current guide, changelog and acceptance matrix
```

### Stage-1 carryover: closed

Nine UI contract defects were reproduced against the accepted contract and fixed; nine
regressions in `tests/TheOverseer.Tests/UiContractCleanupTests.cs` cover them.

1. The Recent-hours ComboBox was bound to `SelectedRecentHours`, which nothing raised, so
   choosing a window from the header menu left the selector showing the previous value
   while the list already used the new one.
2. The left state accent never followed the light toggle (`LeftAccentVisible` was raised by
   nothing), so a redundant accent stayed visible and a wanted one never appeared until the
   view model was rebuilt.
3. A task history strip was reachable with every project history closed but rendered
   without the shared hour axis and legend.
4. A voice failure was computed into `VoiceStatusLine` and rendered nowhere: with
   notifications enabled, a failed provider changed nothing on screen. The health detail
   now carries a failure-only line.
5. Status feedback — tray, startup, navigation, and "settings or position could not be
   saved" — reached `FooterHint`, which no surface rendered, so real failures were
   invisible. There is now a subdued footer at the end of the scrolled content, hidden
   entirely when there is nothing to report.

Demonstrated dead code removed: the superseded `CycleRecentWindowCommand` cycling gesture
(kept alive only by its own test, which now drives the real selector), `SetHealthBrush`
(no caller, and it would have fought the XAML style triggers), a no-op ternary, and an
empty resource dictionary. No XAML binding target was missing and no handler was wired
twice. All 46 fixture renders are byte-identical before and after.

The verification gap is closed: `--hud-composition-check` drives the **real**
`HudComposition` graph four times — startup configuration loading, harness discovery,
voice-provider selection and controller construction — and asserts the state that reaches
the real HUD view model. It covers an empty first run, no harness installed, valid mixed
sources, a bridge added while running (restart required, as documented), and an existing
configuration staying authoritative. 26 checks pass.

### Stage 2: delivered

- **Bridge install hardening.** `install.mjs --apply` now executes the hook command it is
  about to record against a throwaway spool and refuses (exit 2, no write, no backup) on a
  missing payload, a directory, a non-zero exit or a run that publishes no record. The
  probe runs only when recording a path the settings file does not already point at, so
  re-running unchanged stays idempotent. Regressions prove executable binding after
  rebinding to a new extraction whose path contains spaces and non-ASCII characters, that
  the generated command actually publishes a record when invoked with its own argument
  vector, and that a refused rebind leaves a working install intact. Claude suite 21 → 25.
- **dsh install semantics verified, not guessed.** Read from the pinned checkout
  `46a7f68b` and the installed `0.1.7-rc.2` build: `dsh plugin` is a pnpm forwarder, a bare
  local path becomes a `link:` dependency materialised as a Windows directory junction, the
  plugin loads from the original directory, deleting it makes dsh skip the bundle, and
  there is **no dry-run flag**. The integration READMEs and the index were corrected
  accordingly and the "both installers preview" generalisation was removed.
- **Upgrade/rollback.** `Test-ReleaseUpgrade.ps1` seeds and deep-compares every supported
  setting by JSON path, adds a candidate→previous rollback run and a pre-update
  backup/restore boundary, compares a populated spool record-by-record, and forces the
  startup target through the real `StartupRegistration` helper against a fake registry and
  a copied `.lnk`. Its comparison helpers are unit-exercised with 71 assertions including
  43 single-field mutations, each reported at its exact JSON path.
- **Lifecycle safety and graceful exit.** `Test-PackagedRelease.ps1` gained
  `-SkipVisibleChecks`, `-VisibleCheckMonitor` and a shared deferral reason; a requested
  display is honoured only when it exists and is not the primary one. `Test-WindowScaling.ps1`
  was rewritten to choose a display explicitly, seed the isolated run's placement for it,
  assert the window really opened there, and skip the primary display in its transition
  loop unless explicitly allowed. A new `hud-live-graceful-exit` proves a
  normal-composition process exits 0 through the controller with its placement flushed and
  no forced kill.
- **Documents.** `README.md` is now one consistent current guide (675 → 268 lines) with
  the obsolete Codex-only framing, superseded counts and non-existent controls removed.
  `docs/UPGRADE_AND_ROLLBACK.md` is new. `docs/RELEASE_READINESS.md` carries the
  clean-machine protocol and a 28-gate acceptance matrix.

### Candidate identity

```text
archive : artifacts/rc6/OVERSEER-1.0.0-rc.6-win-x64.zip
sha256  : 2ECD384070FE21F8D4E694250B399C2664CEAFC3A276F5873B58BE05F9BA5931
bytes   : 76837418    entries: 568    payload: 568 files, 75 integration assets
identity: FileVersion 1.0.0.0, ProductVersion 1.0.0-rc.6+9f805c8, signing NotSigned
source  : 9f805c85b3bc9ef8147923c214b0ec2f4b84643a (ux/first-use), sourceTreeDirty=false
manifest: artifacts/rc6/build-manifest.json
```

### Verification for the exact rc.6 archive

| Check | Result | Evidence |
| --- | --- | --- |
| Release build | 0 warnings / 0 errors | `artifacts/rc6/verification/build.log` |
| .NET tests | **695 passed**, 0 failed, 0 skipped | `artifacts/rc6/verification/dotnet-test.log` |
| dsh bridge suite | **30/30** | `artifacts/rc6/verification/bridge-dsh-isolated.log` |
| Claude bridge suite | **25/25** | `artifacts/rc6/verification/bridge-claude.log` |
| Packaged smoke (windowed, DISPLAY2) | **44/44, no skipped block** | `artifacts/rc6/smoke/run-20260928T032046Z-a64bf4b5/smoke-evidence-20260928T032046Z.json` |
| Composition check, in the archive | **26/26** | `artifacts/rc6/verification/composition-check-packaged.log` |
| Production-path integration check | **14/14** | `artifacts/rc6/verification/integration-check-packaged.log` |
| rc.5 → rc.6 upgrade + rollback | **25/25**, 89 JSON paths preserved both ways | `artifacts/rc6/upgrade/run-20260928T032514Z-7fcf50d0/upgrade-evidence-20260928T032514Z.json` |
| Upgrade placement (mocked) | **3/3** | `artifacts/upgrade-placement-tests/33c099b52c714915b2c4282cec15b0aa` |
| Window scaling / DPI (DISPLAY2) | native resize, maximize, restore, bounds, Snap hit target | `artifacts/scaling-dev2-184414/window-scaling-evidence.json` |
| Renders | **46 PNGs**, both themes | `artifacts/rc6/smoke/.../hud-fixture` |
| Focus during the live run | 548 samples, **0 owned-foreground**, 0 takeovers | smoke evidence above |
| `git diff --check` | clean | — |

### Defects found and fixed by this round's own checks

1. **The composition check wrote to the user's live data directory.** It composed a real
   controller that saves state on shutdown, and with no `--data-directory` it resolved to
   `%LOCALAPPDATA%\OVERSEER`. It re-saved `state.json` with identical values (same
   `monitorDevice`, same `isExpanded`); `config.json` was untouched. The captured timestamp
   was restored to its pre-run default and the check now isolates itself under the evidence
   root by default and **refuses** an explicit request for the live directory. Recorded here
   because a check that touches live state is exactly the kind of thing this packet forbids.
2. **Placement seeding silently targeted the primary display.** Both `Test-WindowScaling.ps1`
   and `Set-IsolatedPlacement` seeded only `monitorDevice`/`offsetXDips`, which the collapsed
   HUD reads, while the window view places by `WindowMonitor`/`WindowX`. A window-view run
   therefore opened on the display in use. Both modes are now seeded, and the scaling check
   fails loudly if the window is not on its target.
3. **The silent dsh contention flake.** One concurrent dsh run reported a failure in
   `concurrent writers produce whole, non-interleaved lines` while a packaged windowed suite
   was loading the machine; the suite passes 30/30 in isolation, before and after. Recorded
   as a contention-sensitive test rather than hidden.

### Known limitations and deferred checks

- **`hud-live-graceful-close` is never satisfied in a passing run.** While a working tray
  exists every close path hides by design and no script can click the tray's Exit menu. The
  evidence JSON now says so, and `hud-live-graceful-exit` provides the real proof instead.
- The real `rc.5 → rc.6` comparison ran with windowed checks on the secondary display only.
  A machine with a single display defers instead, which is covered by the mocked placement
  harness (0 launches, exit 1), not by a real single-display run.
- `ActivitySparkline` and `ActivitySegments`/`ActivityTooltip` remain in the tree with no
  XAML consumer. They are covered by their own tests, so they were left rather than removed
  in a way that would delete tested behaviour; they are recorded here instead of being
  presented as production UI.
- Existing product limitations now recorded rather than papered over: an
  integration-recorded absolute `bridge.mjs` path does not follow a versioned-directory
  move (re-run the shipped installer or rebind), and a moved install reports `StalePath`
  until the user re-toggles "Start with Windows".

### External qualification gates (BLOCKED, not PASS)

No clean Windows machine/VM or independent account; no browser download through an
owner-approved channel; no live `dsh` or Claude Code host session, so both integrations
stay **experimental**; no signing, distribution-terms or channel decision; no physical
keyboard/Alt+Tab/Snap/drag, mixed-DPI, monitor reconnect, sleep/wake or audible-playback
check. `docs/RELEASE_READINESS.md` lists all 28 gates with each row's exact missing
prerequisite. Nothing unexecuted is recorded as PASS, and public-release readiness is not
claimed.

### Ready for review

Branch `ux/first-use` is review-ready: all local build, test, bridge, packaged, upgrade and
lifecycle checks pass for the exact rc.6 archive, no merge, tag, push or public upload was
performed, and no destructive Git command was used. The user's running HUD, settings,
shortcut, startup entry and real harness configuration were not replaced. One transient
exception is disclosed above: the composition check re-saved `state.json` with identical
values before it was fixed, and the captured timestamp was restored.

## Astra planning — 2026-09-27 — stage-1 cleanup + stage-2 implementation

Stage-entry verdict: **PASS WITH NONBLOCKING NOTES** for starting the combined
implementation goal, not full stage-1 acceptance or public-release approval.
User clarified that this handoff must close remaining stage-1 cleanup and deliver
the bulk of stage-2 implementation in one DeepSeek execution/review round.

Planning baseline: `171f556`; accepted product source `389cb15`. Re-ran Release
build/tests: **686 passed, 0 failed, 0 skipped**, and upgrade-placement control
tests: **3/3 passed**. Placement evidence:
`artifacts/upgrade-placement-tests/76b07269a76f4514a18b6cf4d514f679`.
Independently rechecked RC5 ZIP SHA-256 against the recorded manifest; it matches.
Product source is unchanged since the verified RC5 correction. Prior packaged
integration 14/14 and upgrade 9/9 remain prior evidence, not rerun this pass.

Current executable packet: `.agent/HANDOFF.md`; identical tracked copy appended
to `docs/RELEASE_READINESS.md`. Previous handoff preserved in ignored
`artifacts/planning-stage2/HANDOFF-before-stage2.md`.
Carryover includes a bounded UI/interaction cleanup pass, normal HudComposition
verification and stale user guidance. Stage 2 implements bridge rebind/install
hardening, complete settings upgrade/rollback and executable-path checks, safe
lifecycle/display test controls, first-run qualification and a new clean candidate.
Claude's installer currently permits apply despite a missing payload; stable spool
paths do not prove installed hook commands survive removal of an old extraction.
The handoff treats these as implementation work, not documentation-only caveats.

Clean-machine/browser-download, live host, physical display/audio and owner
distribution/signing/terms gates remain explicit. Missing external prerequisites
must not stop locally executable work or be recorded as passing. No product code,
live HUD, shortcut, user settings or harness configuration changed in this pass.
Next action: DeepSeek executes the combined packet, then Astra reviews the diff
and evidence. Implementation is pending; the planning pass did not dispatch it.

## Astra corrective implementation — 2026-09-27 — PASS WITH NONBLOCKING NOTES

User authorized fixing the re-review findings here. All three are implemented:
startup guard cancellation is synchronous on explicit show/input/shutdown, cleanup
is scheduled even without a prior foreground window, and only the guard-owned style
bit is removed. SourceHealth now uses nullable, source-local evidence and renders
missing evidence as unknown. Upgrade checks emit deferred evidence and exit before
launching either apphost when no display is authorized.

Verification: Release build/test **686 passed, 0 failed, 0 skipped**. Five new
deterministic regressions cover guard cancellation/timeout/style ownership and
unknown/source-local freshness. `scripts/Test-ReleaseUpgradePlacement.ps1` executes
the real upgrade script with only display discovery and process launch mocked:
**3/3 passed**, including zero launches and exit 1 for the deferred branch, secondary
placement for both builds, and explicit primary opt-in. No new test dependency.
Source commit: `389cb15074c287c9a241fd1c5eba045e8cbadad7`. Final diff review found no
remaining blocker within these three fixes. Built clean self-contained internal
candidate `artifacts/rc5/OVERSEER-1.0.0-rc.5-win-x64.zip`, ProductVersion
`1.0.0-rc.5+389cb15`, 566 payload files including 73 integration assets. SHA-256:
`7D3B393F962D93FFFBCB1F7B276CD99AD144B9B25110A01452D5E17480C4E9A0`.
Manifest: `artifacts/rc5/build-manifest.json` (`sourceTreeDirty=false`).

Packaged verification on DISPLAY2 with isolated data:
- **14/14** real-reader/composite/controller integration checks, exit 0, including
  requests, answers, completion, peer survival, recovery and freshness. Evidence:
  `artifacts/rc5/astra-check/integration/run-20260928T011022Z-3f69ec33/integration-report.json`.
- **9/9** RC4 -> RC5 upgrade checks, exit 0. Settings, custom WAV and spool preserved.
  Evidence: `artifacts/rc5/upgrade/run-20260928T011100Z-282e54ee/upgrade-evidence-20260928T011100Z.json`.
- Three mocked upgrade placement cases: no-secondary (0 launches, exit 1), secondary
  and explicit primary opt-in (2 launches each, exit 0). Evidence directory:
  `artifacts/upgrade-placement-tests/30927e4c07ce4b12bf7419326e7b294a`.

These are targeted candidate checks, not a rerun of the full 41-check RC4 smoke or
physical keyboard/Alt+Tab focus qualification. Interactive focus checks remain deferred
to avoid interrupting the user's work. External clean-machine/live-harness, signing,
physical display/audio and rollback gates remain open; this verdict covers the fixes,
not public-release approval. The preceding findings remain below as historical evidence.
Changelog and current handoff updated; no DeepSeek fix continuation is needed for these
three findings. The live app, settings, shortcut and harness configuration are unchanged.

## Astra re-review — 2026-09-27 — CHANGES REQUIRED

Reviewed `af4d42f..b700bf5` on `ux/first-use` (22 files, eight commits).
Scope remains aligned with the previous fix packet. The history recursion, old
attention filtering and project-disappearance episode reproductions now pass.
Typed source health correctly separates instances and explicit evidence timestamps.
Three remaining findings require a small corrective continuation:

1. **MAJOR / P2 — startup activation guard does not honor explicit show intent.**
   `HudApplicationController.RequestShow` activates the HUD without stopping the
   guard or restoring the temporary no-activate style. A tray/second-launch request
   during the first two seconds can therefore be undone by the next timer tick.
   Only preview mouse/key events set `_userTookFocus`, contrary to the guard's
   documented explicit-show exception. Also, `StartForegroundGuard` returns when
   the captured foreground handle is zero *after* the no-activate style was added,
   leaving no timer to remove that style. Centralize guard cancellation/restoration:
   explicit show must do it before activation; absent prior foreground must still
   have bounded cleanup; shutdown must cancel pending work. Cover these branches
   with deterministic tests rather than only passive foreground sampling. These
   paths were found by code inspection, not by seizing the user's live focus.
2. **MAJOR / P2 — upgrade-test deferral is only a message.**
   `scripts/Test-ReleaseUpgrade.ps1:187` marks no-secondary-display runs deferred,
   but execution falls through to both `Invoke-PackagedRun` calls at lines 253/264.
   Without `-AllowVisibleChecksOnPrimary`, this still opens the old and new HUDs
   on the primary display. Guard/return before either launch and emit honest deferred
   evidence. Add a mocked no-secondary test that proves zero process launches, plus
   tests for explicit primary opt-in and secondary placement. No monitor was removed
   and this unsafe branch was not executed on the user's desktop during review.
3. **MINOR / P2 — missing source evidence still advances with the composite clock.**
   `SourceHealth.From` uses its `observedAt` argument (the composite's current time)
   when `EvidenceAt` is default. A not-started source initialized at MinValue thus
   looks freshly timestamped on every publication; a source with an older ObservedAt
   and default EvidenceAt also loses its own fallback time. A nonvisual probe confirms
   three minutes of freshness advancement without any source change. Preserve the
   source's own EffectiveEvidenceAt, and display genuinely absent evidence as unknown
   rather than year-one age or current time. Test both missing and explicit evidence.

Independent verification this round: Release build/test **681 passed, 0 failed,
0 skipped**; dsh **30/30**, Claude **21/21**. The existing isolated view-model probe
now reports history rebuild survived, old pending input visible with RecentHours=2,
and a returning attention project expanded. Probe output is in ignored
`artifacts/astra-review-20260927/rereview-probe.txt`; its aggregate-only legacy health
line is not a test of the new typed source path. The added missing-evidence probes
exercise `SourceHealth.From` directly.

RC4 ZIP SHA-256 independently matches
`E6101FA471657DC11C5FCAA184A59CB6F1D8A4A75AAF623122FDCF747A5C31DE`;
manifest source `c18822d21a28083c4e596314a320d50d1c6c178a`, clean source tree,
ProductVersion `1.0.0-rc.4+c18822d`. Existing package **41/41** and upgrade **9/9**
evidence was inspected, not rerun in full. The health-popup PNG was visually checked.
No new visible smoke or interactive focus test was run because the focus guard is
itself under review. The user's running app, settings, shortcut and displays remain
untouched. Git was clean at review start; only this review ledger is changed in
tracked files. `.agent/HANDOFF.md` contains the updated ordered continuation.

Next action: fix the three findings on the same branch, rerun targeted/full tests,
republish and refresh isolated package evidence, then request Astra re-review.
External clean-Windows, live-harness, signing/terms, physical display/audio and rollback
qualification gaps remain unchanged. No merge, push, live replacement or public
release is approved by this review.

## Astra review — 2026-09-27 — CHANGES REQUIRED

Reviewed DeepSeek's candidate changes `7ac5768..af4d42f` on `ux/first-use`
(41 files). Scope largely matches the authorized simplification/distribution packet;
the following implementation defects prevent acceptance. The earlier claim that
everything locally executable is complete is superseded by this review.

1. **MAJOR / P1 — recursive task-history restoration.** In
   `HudViewModel.RebuildRows`, subscribing to `HistoryToggled` before restoring
   `IsHistoryOpen` calls `RefreshActivityNow -> UpdateAges -> SyncRows` while the
   task collection is incomplete. Opening one task's history, then adding a second
   task, recursively rebuilds the collection. An isolated probe stopped at eight
   nested resets; without the guard this path exhausts the stack. Restore disclosure
   without firing user actions and refresh only after the complete row set exists.
2. **MAJOR / P2 — attention selection can hide known requests.**
   `ShowAttentionCommand` widens Recent to eight hours, but `MatchesView` still
   excludes older requests. A nine-hour-old stale NeedsInput row produces zero rows
   after clicking Needs you. Expose all known pending attention, keeping stale and
   provisional uncertainty visible; do not rewrite the saved Recent window as a
   side effect. Test blocking and async attention beyond the selected time window.
3. **MAJOR / P2 — source health detail uses aggregate freshness.**
   `UpdateHealthProjection` gives every source the newest readable peer's timestamp.
   A `dsh:source-silent` row can say `0s ago` because Codex is fresh. Capability notes
   only recover harness names; the promised source-specific supported/observed/
   unreadable distinctions and separate instances are missing. Carry immutable,
   bounded per-source health/capability/evidence data through the snapshot to the UI;
   do not parse free-text notes or query monitor state from the UI. Test two sources
   with different evidence times, plus two instances of the same harness.
4. **MINOR / P2 — attention episode survives project disappearance.**
   `ApplyFirstAppearanceDefaults` clears episodes only for projects present in the
   filtered row set. Collapse a requesting project, remove its tasks, then introduce
   a new requesting turn: it remains collapsed. Track episode transitions from the
   full snapshot independently of filters; retain manual collapse within one episode
   and permit the specified once-only reveal for a new episode without focus/scroll.

Independent verification: Release .NET build/test completed with **675 passed,
0 failed, 0 skipped**; bridge suites **dsh 30/30, Claude 21/21**. Those tests do not
cover the above interactions. Ignored reproducer and output:
`artifacts/astra-review-20260927/{Probe.csproj,Program.cs,probe.txt}`.
The RC ZIP SHA-256 matches
`DFB84201F7F2F1F4D6E09A30FD64AC834FDE06414E58E15A7ADAA9530BAA5B82`;
source identity is `784733d`. Existing package **40/40** and upgrade **8/8** evidence
was inspected, not independently rerun in full. Later source changes through the
reviewed head are ledger/upgrade-script changes, not app binary changes.

An independently extracted RC ran the finite synthetic HUD smoke for 55 seconds
using isolated settings on `DISPLAY2` (portrait secondary monitor, window origin
`-1000,-166`, DPI 96). Read-only computer-use inspection confirmed that placement;
shutdown and settings reload were clean. No mouse/keyboard input was sent. This is
placement/render/lifecycle evidence, not interactive drag/Snap/DPI or real-harness
qualification. Logs: `artifacts/astra-review-20260927/secondary-smoke.*.txt`.
The user's running build, shortcut and live data/settings were not changed.

Next action: DeepSeek executes the review-fix prompt at the top of
`.agent/HANDOFF.md` on the same branch, adds meaningful regression coverage, then
rebuilds/retests/republishes the candidate and refreshes package evidence. Preserve
the existing clean-machine, live-harness, signing/terms and physical qualification
gaps; no public-release claim, merge, push or live-app replacement is authorized here.

## Astra review-fix continuation — 2026-09-27 (response to CHANGES REQUIRED)

All four findings are closed with regressions, and the newly-enabled secondary-display
lifecycle check exposed one further product defect that is fixed and measured.

### Findings closed

1. **P1 recursive history restoration — fixed** (`c09399f`). Rebuilding a row set
   restored `IsHistoryOpen` through the public setter, which raised `HistoryToggled` and
   re-entered `RefreshActivityNow -> UpdateAges -> SyncRows -> RebuildRows` while the row
   collections were still being populated (the reproducer stopped at eight nested
   resets). Task and project rows now restore disclosure through `RestoreHistoryOpen`,
   which raises only property change; a user toggle is the only thing that notifies.
   Regressions open task and project history, then change membership, order and state,
   asserting disclosure survives with at most one rebuild per changed snapshot.
2. **P2 attention selection — fixed** (`c09399f`). The attention filter was still bounded
   by the selected time window and the command silently rewrote the saved Recent window
   to eight hours, so a nine-hour-old request produced an empty list. The attention
   filter now spans the whole snapshot while keeping stale/provisional marks and the
   honest global counts, and the command changes only the selected view; the saved view
   and window are untouched by `All`. Regressions cover an old blocking request, an
   async needs-you on a working task, and the reset path.
3. **P3 source health detail — fixed** (`737fbe8`, `c09399f`). `MonitoringSnapshot` now
   carries an immutable, bounded `SourceHealth` list (harness, source instance, display
   name, level, reason codes, coverage, its own evidence time and the typed capability
   record), published by `CompositeMonitorSource` in deterministic harness/instance
   order. The popup renders one row per source with its own state, its own freshness and
   the typed distinction observed/supported/not exercised/unsupported/unreadable; the
   aggregate fallback remains only for publishers without per-source data. Tests cover a
   healthy Codex plus a silent dsh (the silent row is `3m ago`, never the peer's
   `0s ago`) and two instances of one harness as separate rows.
4. **P4 attention episodes — fixed** (`c09399f`). Episodes and first-appearance defaults
   are now derived from the whole snapshot over the union of current tasks and tracked
   episodes, not from the displayed subset. A collapsed request that disappears and is
   followed by a new turn is a new episode that reveals once, a manual collapse inside an
   episode and collapse-all are never overridden, and no focus or scroll is forced.

### Additional defect found and fixed

**The HUD took focus while starting up** (`dd51ed8`). Enabling the windowed lifecycle
check on a secondary display exposed it: the HUD became the foreground window about
0.4 s after launch on the visible, `--no-tray`, `--start-in-tray` and non-topmost paths
(542 of 547 samples, one takeover from a live external window). A HUD that owns the
foreground while the user types silently swallows their keystrokes. Two bounded startup
measures fix it: `WS_EX_NOACTIVATE` from window creation until the first two seconds
settle (the original extended style is restored afterwards, verified
`0x08040108 -> 0x00040108`), plus a 20 x 100 ms guard that returns focus to the window
that had it and stops for good on any user interaction or explicit show request.
Measured with a foreground probe on both displays: **0 owned samples out of 40** in all
four configurations, against 542/547 before the fix.

### Visible-check placement (review instruction)

Windowed checks must not open on the display the user is working on. A new
diagnostics-only argument `--placement-monitor DEVICE` steers a check's own window
(no setting is written, the saved placement is untouched), and
`Test-PackagedRelease.ps1` / `Test-ReleaseUpgrade.ps1` enumerate displays, send every
windowed mode to a non-primary monitor, and seed each isolated lifecycle data
directory's `state.json` with that monitor. With no non-primary display they **defer**
the windowed checks with an explicit gap instead of opening them on the display in use
(`-AllowVisibleChecksOnPrimary` covers a single-display qualification VM). The upgrade
comparison never passes a new argument to the previous build — a release older than the
option would reject it with a blocking usage dialog (observed as
`previous-build-runs exit=-1`) — so its placement is steered through the seeded saved
state, which every release understands.

### Candidate evidence (review-fix round)

Packaged source commit `c18822d21a28083c4e596314a320d50d1c6c178a`. Commits after it are
documentation and `scripts/` only, and scripts are not part of the archive payload; the
repository HEAD is a later docs-only commit.

```text
archive : artifacts/rc4/OVERSEER-1.0.0-rc.4-win-x64.zip
sha256  : E6101FA471657DC11C5FCAA184A59CB6F1D8A4A75AAF623122FDCF747A5C31DE
bytes   : 76815286   entries: 566   integration assets: 73
identity: FileVersion 1.0.0.0, ProductVersion 1.0.0-rc.4+c18822d, signing NotSigned
manifest: artifacts/rc4/build-manifest.json
smoke   : artifacts/rc4/smoke/run-20260927T232228Z-5d48094d/smoke-evidence-20260927T232228Z.json
upgrade : artifacts/rc4/upgrade/run-20260927T232702Z-7041adaf/upgrade-evidence-20260927T232702Z.json
```

| Check | Result |
| --- | --- |
| Release build | 0 warnings / 0 errors |
| .NET tests | **681 passed**, 0 failed, 0 skipped |
| dsh / Claude bridge suites | **30/30** and **21/21** |
| Renders (`--hud-fixture`) | **46 PNGs**, both themes, 300/360/480/720 DIP, attention filter, open history, and the open health detail |
| Production-path check | **14/14**, in-process and from the extracted archive, now including "the released file stops degrading the source" (Codex back to live) |
| Packaged smoke | **41/41 passed, 0 failed, no skipped block**, windowed checks on `\\.\DISPLAY2` |
| Upgrade `ad35e6d` → rc.4 | **9/9 passed** on copies of one data directory |
| Focus probe | 0 owned foreground samples of 40 per configuration on both displays |
| `git diff --check` | clean |

### Still open (unchanged external qualification)

No clean Windows machine or VM, no live `dsh` observer attach and no Claude Code CLI,
no signing or distribution decision, no physical DPI/Snap/monitor-removal/sleep-wake or
audible playback check, and no rollback execution. Public-release readiness is not
claimed; the candidate is an internal RC.

## Planning update — 2026-09-26

User requested a repository-grounded diagnosis combining HUD simplification with
a credible public-release path. Baseline 075356e; no product code, running app or
settings changed in this planning round. See docs/RELEASE_READINESS.md for confirmed
findings, concrete UI contract and two proposed implementation/qualification iterations.
The ignored .agent/HANDOFF.md now contains the executable implementation packet;
its predecessor is backed up under artifacts/planning-20260926/.
Confirmed release gaps include mutex/show-signal scope mismatch, smoke checks
without real composite-to-UI state assertions, absent bridge assets in the ZIP,
unsigned apphost and stale current documentation. 668 tests/17 scoped package checks
are previous recorded evidence, not rerun or claimed as full public qualification.
Next action: review/authorize the packet, then execute the whole implementation goal;
public publishing, distribution terms/signing and external-machine tests remain separate.
The sections below retain historical implementation and deployment evidence.

## Current
Plan: Post-V1 — Usable public release candidate (internal RC 1.0.0-rc.1)
Goal: simplify the HUD, fix isolated-instance scope, verify the candidate through the
real monitor path, distribute the optional bridges, and identify the candidate by
version and source commit.
Branch: `ux/first-use`. This round's base is `7ac5768`; the planning packet is
`docs/RELEASE_READINESS.md` plus the ignored `.agent/HANDOFF.md`.

### Status: implementation complete for everything locally executable; NOT a public release

Public-release readiness is **not** claimed. The gates that still require
environments this machine does not have are listed below as explicit qualification
gaps. No merge, tag, push or public upload was performed.

### Delivered

- **Simplified HUD** (`6ca5d17`). One attention-first summary whose halves open the
  matching filter with *All* always visible; one clickable health control with
  per-source detail; explicit Recent-hours selection; per-project and per-task history
  disclosure with the shared axis and legend inside the disclosed area; first-appearance
  expansion (attention/active expanded, settled collapsed) with the user's choices and a
  once-per-episode attention reveal preserved. Removed: duplicate status headline,
  decorative linework, constant voice sentence, permanent instruction line. Body 13 DIP,
  secondary 12 DIP, collapsed HUD 300x96 DIP. Closed history stops the geometry
  projection only — observation, recording and voice are untouched.
- **Source-aware health wording** (in `6ca5d17`). Composite reasons are prefixed with
  the harness, so the HUD now matches codes with or without that prefix. A degraded
  `dsh`/Claude source is no longer rendered as healthy, and the empty state no longer
  claims a Codex-only view.
- **One instance scope** (`051f4e7`). `InstanceScope` derives the single-instance mutex
  and the show-request event from one identity: Windows user plus canonical data
  directory. Two different data directories can no longer fight over one slot or signal
  each other, which is what makes an isolated packaged lifecycle check possible while a
  real HUD keeps running.
- **Production-path candidate check** (`debc035`). `--hud-integration-check` invents a
  Codex rollout home and two bridge spools, drives them through the real readers,
  composite and controller, and asserts the applied UI state: working → attention →
  answer → completion; async attention while working (and that an unrelated tool output
  cannot answer it); honest per-source degradation with the peer still publishing; no
  monitor-stopped latch; recovery on clean data; freshness advancing via the new
  `HudViewModel.AppliedSnapshots` counter. 13/13 checks pass locally.
- **Distribution and identity** (`a49403b`). The archive now ships both bridges
  (sources, notices, installers, tests — 73 files) plus an index that states
  prerequisites, the data-directory spool location and the required restart. The archive
  carries `release.json`; numeric `FileVersion`/`AssemblyVersion` is separate from the
  release identifier and source commit; publish validation compares the numeric version
  exactly. `Test-PackagedRelease.ps1` verifies identity, runs the shipped bridge suites
  from the extraction alone, runs the integration check, and verifies that a previous
  release's settings, mapping, disabled source, recording, placement and spool survive.
  `scripts/Test-ReleaseUpgrade.ps1` compares an older archive with this candidate on
  copies of one data directory.
- **Docs** — `README.md` (release identity, integrations, smoke coverage),
  `CHANGELOG.md`, `docs/UI_SPEC_v0.2.md` (placeholder replaced by the implemented
  contract) and `integrations/README.md`.

### Preserved

The user's running HUD, `%LOCALAPPDATA%\OVERSEER` settings, desktop shortcut,
startup entry and real harness configuration were never modified. All checks ran with
`--data-directory` under `artifacts/`. No merge, rebase, reset, tag or push; `main`
is untouched; the stale `agent/ui` and `agent/voice` worktrees were left alone.

### Candidate evidence (packaged)

Packaged source commit `784733dba63e84c3b7ed61ec58fe8b49259c9ef7` (`ux/first-use`).
Repository HEAD at the time of writing is a later documentation-only commit; the
archive below was built from `784733d`, and `release.json` inside it records the same
commit. Scripts changed after that commit are not part of the archive payload.

```text
archive : artifacts/rc1/OVERSEER-1.0.0-rc.1-win-x64.zip
sha256  : DFB84201F7F2F1F4D6E09A30FD64AC834FDE06414E58E15A7ADAA9530BAA5B82
bytes   : 76810259
entries : 566   (payload 566 files, 73 of them integration assets)
identity: FileVersion 1.0.0.0, ProductVersion 1.0.0-rc.1+784733d, signing NotSigned
manifest: artifacts/rc1/build-manifest.json
smoke   : artifacts/rc1/smoke/run-20260926T231953Z-9004e47d/smoke-evidence-20260926T231953Z.json
upgrade : artifacts/rc1/upgrade/run-20260926T232512Z-9b665477/upgrade-evidence-20260926T232512Z.json
```

| Check | Result |
| --- | --- |
| Release build | 0 warnings / 0 errors |
| .NET tests (`dotnet test -c Release`) | 675 passed, 0 failed, 0 skipped |
| dsh bridge tests (`node --test`) | 30 passed, 0 failed |
| Claude bridge tests (`node --test`) | 21 passed, 0 failed |
| Renders (`--hud-fixture`) | 44 PNGs, both themes, 300/360/480/720 DIP widths, attention filter, open history |
| Production-path check (`--hud-integration-check`) | 14/14 passed, in-process and from the extracted archive |
| Packaged smoke (`Test-PackagedRelease.ps1`) | **40/40 passed, 0 failed, no skipped block** |
| Upgraded from `ad35e6d` (`Test-ReleaseUpgrade.ps1`) | 8/8 passed; settings, mapping, disabled source, recording, spool and identity compared |
| `git diff --check` | clean |

The packaged smoke now includes: archive identity and integration payload, 44 fixture
renders, `--hud-smoke`, `--hud-integration-check`, `node --test` for both shipped
bridges, voice smoke with the owned loopback endpoint, the shipped voice pack check,
synthetic and real `--monitor-debug`, the legacy-settings upgrade checks, and the full
HUD lifecycle (60 s live run with no focus takeover, second instance exits politely,
close hides to tray, second launch restores the same window, persisted state, tray
startup, and the `--start-in-tray --no-tray` visible fallback). The lifecycle block is
no longer environment-blocked: the scoped instance identity lets it run beside a real
HUD.

Known test flake, unchanged by this round and not caused by it:
`HudWindowTests.WholeSummaryCapturesDragButHeaderButtonsKeepTheirClicks` was seen
once to fail on its `Mouse.Captured` assertion while the build was otherwise failing
under full-suite load, and passed in the immediately following full runs and in
isolation. No test was skipped, weakened or hidden; the same flake is recorded in the
historical section below.

One new product defect was found and fixed by this round's own check: a tracked
rollout file that disappears was retained forever in its failure backoff, so the
source reported `read-failure` on every pass, the HUD stayed at limited coverage, and
its task could never be observed again. Files that are absent are now released after
`MonitorOptions.MissingFileRetention` (60 s default) and re-admitted if they return;
the integration check runs with a 12 s retention and asserts that the reason clears.

### Qualification gaps (not claimed as delivered)

1. **No clean Windows machine or VM.** The archive was never downloaded through a
   browser, extracted and run on a machine without this checkout, the workspace SDK or
   cached packages. `Test-PackagedRelease.ps1` sanitizes the child environment and runs
   from an unrelated working directory, which is not the same evidence.
2. **No live harness qualification.** `dsh` is installed (`0.1.5-rc.3`) but the
   observer plugin was never installed into the live profile, and no model-backed
   session was observed; Claude Code CLI is still not installed on this machine. Both
   bridges remain contract-tested and are labelled experimental in
   `integrations/README.md`.
3. **Unsigned.** The apphost is `NotSigned`; browser-download reputation, distribution
   terms and asset-redistribution rights remain owner decisions.
4. **Physical checks not performed:** real tray interaction, mixed-DPI monitor
   combinations and movement, Snap behaviour, monitor removal/reconnect, sleep/wake and
   audible playback by ear.
5. **Rollback** from this candidate back to the previously deployed build was not
   executed; `Test-ReleaseUpgrade.ps1` covers old → candidate on copies of one data
   directory.
6. **Shortcut and startup targets** were only inspected read-only; the upgrade check
   does not rewrite a user shortcut or the Run entry.

### Acceptance matrix (RELEASE_READINESS.md)

| Gate | State | Evidence |
| --- | --- | --- |
| Start | partial | Archive extracts and runs from an unrelated working directory with a sanitized environment (no SDK, no checkout) — 40/40 packaged checks. A real browser download on a clean OS was not performed. |
| Empty machine | done | A HUD with no bridge installed starts, shows Codex only, and `Projects & sources` states the setup action. |
| Monitoring | done | Live tasks reach the actual HUD after startup: `--hud-integration-check` (applied UI state) and the packaged 60 s live run. |
| Attention | done (Codex async qualified) | Bridge approval asked/decided and Codex async input while working; the async request is not cleared by an unrelated tool output and clears at the turn boundary; per-source unavailability is disclosed. |
| Harnesses | **blocked** | No live `dsh` session and no Claude Code CLI on this machine. Contract tests only; both integrations are labelled experimental. |
| UI | done locally | 44 renders (both themes, 300/360/480/720 DIP, short/tall, light at max width, attention filter, open history) plus WPF tests for hit regions, one scroller, last-row reach and focus. Native Snap/DPI/monitor movement remain physical. |
| Lifecycle | done | Same-scope second launch restores; a different scope cannot signal the user's HUD (regression test); hide, tray start, graceful exit and persisted state verified in the packaged run. |
| Recovery | partial | Malformed/absent producer data degrades only that source, is disclosed per source, does not latch, and recovers on clean data. Sleep/wake and monitor removal/reconnect are physical. |
| Voice | partial | Loopback smoke, shipped pack check and mute persistence pass. Audible playback by ear was not performed in this round. |
| Upgrade | partial | Old → candidate on copies of one data directory: 8/8. Rollback to the previous build was not executed. |
| Distribution | partial | Unique version/source hash, private-payload audit, bridge assets/notices, correct `NotSigned` status. Download reputation and distribution terms are owner decisions. |

### Next actions (for Astra review)

1. Review the diff and the new checks; decide whether the presentation contract is met.
2. Re-run `scripts/Publish-Release.ps1` / `Test-PackagedRelease.ps1` /
   `Test-ReleaseUpgrade.ps1` from the reviewed commit (the packaged source commit is
   recorded above and in `release.json`).
3. Decide the distribution/signing path, then complete the blocked gates (live `dsh`
   and Claude qualification, clean machine, physical DPI/Snap/sleep checks, audible
   playback, rollback) once those environments exist.

## Historical — project-centered round (superseded by the 2026-09-26 round)

Plan: Post-V1 — Project-centered multi-harness HUD
Goal: One integrated implementation/review round for project grouping, activity strips,
responsive HUD controls, and an offline baseline voice
Branch: ux/first-use
Product base: d869db6 (custom HUD implementation: 5521980)
Last packaged implementation: **ad35e6d** (`ux/first-use`), including the monitor
lifetime regression fix. Running from artifacts/overseer-live-ad35e6d; desktop
shortcut updated and live task updates verified after replacement.

## Current Plan Status
- Astra handoff: reviewed and corrected in ignored `.agent/HANDOFF.md`
- DeepSeek implementation: **complete for the five ordered areas**; every item of the
  26e642b review is addressed or explicitly qualified below.
- Verification for this round: Release build **0 warnings / 0 errors**; **641 .NET
  tests passed**, 0 failed, 0 skipped; **30/30 dsh** and **19/19 Claude** Node tests;
  **38 render fixtures** rendered and inspected from committed source; clean-source
  publish from `915b735` (sha256 `2F578ECF…3E10B`); packaged smoke **17/17 passed**
  with the HUD lifecycle block environment-blocked (exact reason below).
- Astra source review: **PASS WITH NONBLOCKING NOTES after the 2026-09-25 fixes**.
  The confirmed monitoring defects were fixed and adversarially rechecked; see the
  latest review below. Release qualification remains pending, not implicitly passed.
- Preserved: the user's running HUD (pid 38960) and live `config.json` were never
  modified; provider is still `pack`, still muted, byte-for-byte the same file.
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

Next action: confirm input-alert/scroll behavior in the running updated HUD and finish
remaining packaged lifecycle and live harness/voice qualification.
The frozen original four-plan V1 scope documents remain unchanged; this is an explicit
user-requested post-V1 extension.

## Project-centered round — execution (2026-09-24, partial)

### Follow-up — monitor offline regression, 2026-09-25

User reported the deployed HUD offline. The live log confirmed
`hud-monitor-monitor-stopped` immediately after launch. The prior startup fix
changed StartAsync to initialization completion, but the controller still treated
that task as monitor lifetime and permanently rejected subsequent snapshots.
The earlier process-responsive check did not establish live monitoring health.

IMonitorSource now explicitly separates initialization from Completion. The
controller observes both; the composite remains alive through source-local faults,
and Codex lifetime termination publishes unavailable source health immediately.
A real controller/composite/adapter regression checks startup, later snapshots,
source failure and disposal. All 668 Release tests passed; build 0 warnings/errors.

Clean-source package **ad35e6d**: artifacts/monitor-lifetime-release/OVERSEER-v1-win-x64.zip,
SHA-256 323B03D64500A919BD3AF8449E02FAAA9F5C4B60730F04B9A31D62C4CC7F6804.
Old HUD exited through its Exit menu. New process 27424 runs from
artifacts/overseer-live-ad35e6d and desktop shortcut points there. Configuration
is byte-for-byte unchanged, voice remains muted/pack. Backups retained in
artifacts/deployment-ad35e6d; old builds retained for rollback.
Live accessibility and visual checks show Working, two project/task rows and
40 tracked tasks, updated 0s ago. Coverage still honestly reports discovery-backlog
and input-detection limitations; this is distinct from the fixed offline latch.
No new monitor-stopped error appeared after restart. This replaces the previous
process-only deployment check; full live multi-harness/voice qualification remains open.
Fresh-extraction package smoke: 17/17 passed (five-second finite HUD/monitor modes,
38 renders, isolated voice/pack checks). HUD lifecycle script block skipped to
preserve the user instance; the actual replacement was separately checked twice
through native accessibility, with fresh task updates both times.
Evidence: artifacts/monitor-lifetime-release/smoke/run-20260926T055551Z-384d21f7/.

### Follow-up — missed input prompts and scrolling, 2026-09-25

Deployment after explicit user request: package hash verified, old 5521980 instance
closed through its orderly shutdown handler, and 763c688 launched with the same default
data directory. New process 51308 is responsive with an OVERSEER window; only one
OVERSEER process remains. Desktop OVERSEER.lnk now points to the new build. No existing
configuration value changed; the new app added only project/source configuration fields.
Voice remains muted with the pack provider. Settings, state and old shortcut backups:
artifacts/deployment-763c688-20260925-223226. Old binaries retained for rollback.
This confirms replacement/startup, not the outstanding audible/multi-harness/DPI checks.

Base 79345b2. Current desktop metadata was observed to persist input requests as
`response_item/function_call` named `request_user_input_async`, with a call ID but
without a turn ID. The old parser treated this as opaque activity and incorrectly
described all Codex input requests as unobservable. Both synchronous and async input
tool names now produce input attention from structural metadata only. Synchronous
tool responses clear matching blocking requests; async acknowledgements do not.
Question/options/answer content is never inspected. Explicit async answer correlation
is still limited to observable resolution events or the existing turn boundary.

Removed the nested task scroller used in short windows: all expanded content shares
one vertical scroller, with header controls/summary pinned. Wheel events route through
the same surface over project/task rows. No change to voice mute/provider settings.

Verification: Release build 0 warnings/errors, **667/667 .NET tests**; 38 fixture
renders, representative short/dark and tall/light images inspected; diff check clean.
The running process was observed at artifacts/overseer-chrome-5521980, older than
these changes. It was not replaced during source testing. Updated package preparation
is recorded separately; live input-alert and scrolling confirmation remain desirable.

Clean-source package from **763c688**: artifacts/input-scroll-release/OVERSEER-v1-win-x64.zip,
SHA-256 EAE3393ED03FD1FBE3D5BD310FB9D009C1352759C2AC5FD57B2141B0FF7DC5A0.
Manifest confirms sourceTreeDirty=false. Fresh-extraction smoke **17/17 passed**
(38 renders, isolated HUD/voice/pack checks, synthetic and readable real monitor).
Evidence: artifacts/input-scroll-release/smoke/run-20260926T052833Z-5c069e7d/
smoke-evidence-20260926T052833Z.json. HUD lifecycle explicitly skipped to preserve
the running user HUD; no audible or live multi-harness qualification claimed.

### Astra implementation cleanup and adversarial review — 2026-09-25

Reviewed base: e5a6a0f on ux/first-use. User explicitly authorized direct cleanup and
fixes, followed by a final adversarial review. No product redesign, scope expansion,
new project, framework or dependency. Frozen source documents remain unchanged.

Confirmed findings fixed:

1. **MAJOR — startup stalled at Codex.** Its lifetime task was awaited as adapter
   initialization, preventing subsequent bridge startup. Start now returns after launch;
   a regression composes the real Codex monitor with a second adapter.
2. **MAJOR — custom roots and incomplete discovery.** Retain the discovered local
   path through ingestion (excluded from JSON/diagnostics), deduplicate root aliases,
   distinguish disabled sources from enabled capacity, propagate caps/missing explicit
   roots into partial counts, and suppress complete coverage even when visible sources
   are healthy.
3. **MAJOR — shared bounds were not enforced.** Codex now receives its share before
   work starts, including tracked conversations/files. Remove upward share floors and
   the identity-reader floor. Bridge payload reads share one allowance across health,
   fingerprints, rotated events, live events and current state. Partial records and large
   snapshots progress across polls; rotation enumeration is bounded before sorting.
4. **MAJOR — misleading recovery/health.** Cache snapshot identity only with its parsed
   result; malformed replacements cannot return old healthy data. Preserve drop evidence
   on invalid health JSON, saturate overflow, and report partially recovered rotations
   as loss. Select the matching rotation rather than replaying the oldest retained file.
5. **MAJOR — silence could stay healthy.** A monotonic-clock watchdog ages adapters
   after two minutes without publication, including when every source stops; fresh
   evidence restores them. Empty authoritative snapshots also expire by their lease.
6. **MINOR — lifecycle races.** Bridge startup is idempotent, budget application is
   restricted to startup, and timed-out disposal does not dispose a gate still owned
   by a filesystem read.

Final verification: Release build **0 warnings / 0 errors**; **659/659 .NET tests**,
including 18 new runtime/adversarial regressions; **30/30 dsh + 21/21 Claude** tests;
git diff --check clean. Existing regression tests retained. The second review covered
small allowances, hostile health shapes/overflow, partial records, rotation history,
malformed snapshot replacement, discovery omissions and silence/recovery. No remaining
confirmed blocker in the reviewed paths. UI/voice code was not redesigned in this round.

**Release hold remains:** the existing 915b735 package does not contain these fixes.
No publish or packaged lifecycle smoke was rerun. Previous smoke explicitly skipped HUD
lifecycle; live dsh attach, Claude Code compatibility, physical tray/multi-DPI and audible
voice still require qualification. Do not interpret green unit/fixture tests as those
checks. Running HUD, desktop shortcut and live harness settings were not changed.

### Astra repository review — e5a6a0f

Verdict: **CHANGES REQUIRED**, materially closer to completion. Project expansion,
source/voice settings, evidence-carrying strips, close/header changes, snapshot recovery
and a clean-source packaged candidate now exist. No significant scope creep or new
framework/project dependency was found; frozen documents remain unchanged.

Remaining findings from inspected integration boundaries:

1. **MAJOR — configured spool roots are not used by the reader.**
   HarnessSources.DiscoverDetailed enumerates configured paths, but creates a
   SourceInstance carrying only harness/id/display/enabled. BridgeMonitorAdapter's
   SpoolDirectory reconstructs the default data-directory path, so a source discovered
   exclusively under a custom root is unreadable (or reads an unrelated default source
   with the same ID). Preserve resolved local spool location through composition,
   deduplicate aliases to the same root, and test actual adapter ingestion from a
   nondefault root rather than discovery alone.
2. **MAJOR — the claimed shared budget excludes Codex.** CodexMonitorAdapter does
   not implement IBudgetedMonitorAdapter; CodexMonitor is constructed with the full
   MonitorOptions allowance before the composite divides a budget among adapters.
   With three sources, Codex can still read 4 MiB plus two bridge shares (~1.33 MiB
   each), instead of 4 MiB aggregate. Divide/enforce before Codex construction or
   provide a real budget boundary. SourceBudget.Share also floors shares upward,
   so small configured aggregates may be exceeded. Prove total work across actual
   adapters under low as well as default limits.
3. **MAJOR — discovery truncation is discarded.** Composition calls Discover(),
   throwing away DiscoverDetailed.Capped. Composite knows only its own eight-source
   truncation; a lower configured discovery cap or per-root cap can omit enabled
   producers without making global coverage incomplete. Carry this evidence into
   health/counts and suppress unqualified all-complete. Disabled entries currently
   consume discovery slots too; keep them from crowding enabled sources out silently.
4. **Release qualification still pending.** Inspected package evidence explicitly
   has hudLifecycleSkipped=true. The running user's HUD prevented that test block;
   this is a pending release gate, not a passing lifecycle result. dsh live attach,
   a compatible Claude Code hook environment, physical tray/DPI and audible playback
   remain disclosed qualifications. No live settings were changed during review.

Fresh independent verification: Release build 0 warnings/errors, **641/641 .NET**
tests, **51/51 Node** tests (30 dsh + 21 Claude), diff check clean. The archive's
SHA-256 independently matches 2F578ECF497DC2E7158F28B9BA46965AF21ECF3E82726A935C3D54947813E10B;
manifest confirms clean source 915b735. The 17 smoke checks are prior recorded evidence,
not rerun in this review; HEAD differs from that source only in documentation.

Git audit: ux/first-use at e5a6a0f, 21 commits since 3152ec5, clean at review start;
main unchanged at 663caf5; no remote configured. agent/ui and agent/voice worktrees
are clean, and git cherry reports all their commits patch-equivalent in ux/first-use
(no unintegrated branch changes detected). This review adds only this ledger edit;
no product/package/app/shortcut changes. Complete the focused fixes above, rerun
checks, republish changed implementation, then finish lifecycle/live qualification.

### DeepSeek completion — response to "inspected 26e642b" (2026-09-24)

**Status: implementation complete for the five ordered areas; ready for Astra review.**
Branch `ux/first-use`, packaged implementation head `915b735` (this smoke record is a
documentation-only commit on top).
No merge, no push, no history rewrite; `main` untouched; the running package and
shortcut were not replaced.

Commits in this round (oldest first):

```text
48cc087 fix: recover bridge state from producer snapshots
3400877 fix: share monitoring budgets and aggregate source capability
b8b2402 fix: make bridge recovery deterministic and self-review cleanups
c3c3a80 fix: keep bridge requests visible over continuing tool activity
4aa7a9e feat: declare honest per-harness bridge capabilities
8b2e0b2 docs: describe bridge recovery, source budgets and freshness rules
093a6d2 docs: changelog for bridge recovery and shared budgets
628bc32..e2c961a voice: baseline default, bounded SAPI workers, provider choice,
                     selected-provider-only playback, real-engine smoke  (5 commits)
7e1a6ce..c8aac75 HUD: expandable project surface, evidence-carrying strips, legend,
                     header/menu/close/size matrix, Projects & sources  (4 commits)
92c111f feat: share one monitoring budget and bounded discovery in composition
```

#### Area 1 — bridge state/recovery (review item 1)

- The producer's atomically published `current.json` is now parsed
  (`HarnessBridgeCurrentState`) and applied as the **authoritative
  resynchronization**: a late attach, a dropped record or a rotated event file no
  longer leaves a source permanently incomplete, and an empty document is a complete
  statement about an empty source rather than a missing one.
- Coverage is split: `CurrentStateComplete` (recoverable) versus `HistoryGap`
  (permanent, per epoch). A gap never becomes observed activity, is disclosed while it
  can still affect the displayed hour, and stops degrading the source afterwards.
- `FirstAttach` is gone as an unfalsifiable flag. Recovery paths: an authoritative
  document, a `session/start` observed without a gap, and epoch change handling that
  resets ordering without inventing history. Sessions the producer no longer lists are
  retired instead of looking like ongoing work, and become live again on new evidence.
- Liveness and action evidence are separate: heartbeats refresh liveness only, never the
  action or its category. Actions older than the ten-second lease become unknown
  coverage while the task may still be working.
- Terminal outcomes are explicit: completed/failed map as before, cancellation stays
  silent, and a **missing or unrecognized outcome is never a completion** — it is an
  explicitly unknown end that lowers coverage.
- Rotation: the unread tail of the rotated file is drained before the new file starts,
  and same-size replacement is detected by file identity (creation time plus a bounded
  head fingerprint), not by length. Records the snapshot already folded are
  `Superseded`, not reported as duplicates or out-of-order.
- **Three producer/bridge defects found and fixed**, each proven by a regression test:
  an event-file rotation swallowed the record that triggered it; the dsh published
  sequence reused host `seq` numbers, so every host event the observer deliberately
  does not export (most of them) looked like a lost record (`sequence` is now a
  contiguous publication counter; `eventId` identity and replay/hydration semantics
  unchanged); and the Claude bridge counted a failed append with the name
  `droppedWriteFailed`, which was **missing from the health allowlist**, so the counter
  was silently discarded and the record vanished with neither a landed line nor a drop
  — the exact "9 published records, found 8" symptom the previous review reported under
  concurrency load. The name is allowlisted, a failed append no longer consumes a
  per-session sequence number, the concurrency invariant now counts every `dropped*`
  counter instead of lock timeouts only, and a structural test asserts that every
  counter name the bridge can emit is in the allowlist. Reproduced at 1/10 runs before
  the fix under CPU load; 30/30 green afterwards (evidence below).
- **Actual producer output is the fixture.** `integrations/deepseek-harness/fixtures/
  generate-spool-fixtures.mjs` drives the shipped state machine and spool writer to
  produce `fixtures/spool/{live,rotated,dropped}`, and
  `tests/TheOverseer.Tests/BridgeProducerFixtureTests.cs` consumes them end to end.
- Also fixed while reviewing: a tool start/end or plan marker observed while an
  approval or input request was unanswered overwrote the waiting state and hid the
  fact that a human was the blocker.

Evidence: `HarnessBridgeTests` (identity, hydration, ordering, epochs, drops, clock
skew, concurrency/correlation, late events, retirement, recovery),
`CompositeMonitorTests` (same-size replacement, cached document, budget share,
freshness, capability aggregation, bounded discovery) and
`BridgeProducerFixtureTests` (live/dropped/rotated spool, evidence-time freshness,
per-pass budget bound, capability disclosure, no replayed attention).

#### Area 2 — composite monitoring (review item 2)

- One aggregate per-pass read allowance and one tracked-item allowance are **divided**
  between enabled sources (`SourceBudget.Share`), never multiplied by them; at most
  eight source instances are monitored, truncation is reported as a health reason, and
  the bridge adapter re-bounds a running reader when its share is applied.
- Discovery is bounded per root and in total, deterministic, honours explicitly
  configured `harnessRoots` from config, and still excludes disabled sources.
- Freshness is the producer's evidence time: `SourceSnapshot.EvidenceAt` plus
  `EffectiveEvidenceAt`, and the composite's `LastSuccessfulReadAt` is the newest
  evidence among *readable* sources. Repolling an idle spool no longer looks like a
  fresh read, and a fresh peer can neither refresh a dead source nor donate
  capabilities on its behalf.
- Capabilities are merged across readable sources with a per-harness note instead of
  "the last healthy source wins"; a bridge now declares what it actually provides
  (correlated approval/input, explicit outcomes, no titles, no runtime activity), and a
  one-shot hook source states that absence of events proves neither idle nor
  disconnection.

#### Area 3 — baseline voice (review item 3)

- A brand-new data directory selects the **offline Windows speech baseline** and stays
  muted; an existing `config.json` is never reset (a file without a `provider` key
  keeps pack, and explicit provider/mute/pack/endpoint/events/clips survive).
- The selected provider is the only provider used. The automatic TTS→pack fallback is
  removed from composition and from preview, assigned recordings still win, and a
  failing selected provider is silent and reported rather than swapped.
- `Light & voice…` offers the three providers, a visible baseline availability line,
  an explicit **Use baseline** action, and opening it neither synthesizes nor sends a
  request.
- SAPI synthesis is bounded: at most two STA workers (`speech-busy` beyond that),
  cancellation and the deadline observed while waiting, and each worker deletes its own
  temporary WAV in its own cleanup, so timed-out workers cannot leak files or threads.
  Failures/timeouts are never cached, and the cache is bounded and keyed by script,
  engine and format version.
- Real-engine evidence on this machine (English voice `Microsoft Zira Desktop`):
  packaged `--voice-smoke` exit 0, and the standalone run reported
  `engine: sapi available=True code=ok`, `synthesized: bytes=80278 valid=True
  code=ok rate=22050 channels=1 bits=16 seconds=1.82`, `preview: created=True
  played=True` and "no pack clip was ever played (0)".

#### Area 4 — project/HUD presentation (review item 4)

- Projects are real expandable rows (name, harness badges, working/needs-you counts,
  hour strip, chevron) ordered attention → active → recent with stable identity
  tie-breaks; expansion is keyed on `ProjectId` and survives snapshot rebuilds, and
  collapsing never stops observation, history or voice.
- The strip transport keeps category, evidence strength, coverage, bin start/end,
  detail code, concurrent task count and a mixed flag; unknown is a hatch that can
  never be confused with idle, inferred bins are capped, and the tooltip and automation
  name carry the interval detail. An eight-entry legend states "Unknown = no
  observation, not idle".
- The competing global count chart is gone from every surface (retained internally,
  test-covered); each distinct project is projected once per refresh instead of once
  per task.
- Header/menu/close/size matrix completed; navigation is harness-qualified and an
  unsupported harness offers its id instead of advertising `codex://`. `Projects &
  sources…` lists discovered sources and project buckets with additive persistence.
  A stored source enable/disable applies at the next start, and the window says so.

#### Area 5 — release gates (review item 5)

```text
build      Release, committed source 915b735: 0 warnings / 0 errors
tests      Passed! Failed: 0, Passed: 641, Skipped: 0, Total: 641
node       deepseek-harness 30/30 passed; claude-code 21/21 passed, and 30/30
           consecutive runs green under concurrent .NET test load (the previous
           round saw this suite intermittently red; the cause is fixed above)
renders    38 fixtures (19 per theme), inspected: dark/light expanded, one-project,
           activity-hour, collapsed-projects, 320x220, 440x620, 640x260, settings
publish    scripts/Publish-Release.ps1 from clean committed source 915b735
           OVERSEER-v1-win-x64.zip 76,658,061 bytes, 492 entries,
           sha256 2F578ECF497DC2E7158F28B9BA46965AF21ECF3E82726A935C3D54947813E10B
           (an earlier archive from 92c111f, sha256 B73A3A2D...AD5679, was replaced
           after the Claude bridge fix, because the shipping payload includes the
           integration assets that fix changed)
smoke      17 checks, 0 failed, run against that exact archive
           (hud-fixture 38/38 non-empty renders, hud-smoke,
           voice-smoke + owned loopback, voice-pack-check, monitor-debug synthetic
           and real, packaged payload/entry checks)
live codex --monitor-debug --duration 15 against the real Codex home: exit 0,
           40 files tracked, 39.9 MB read, lifecycle/activity live-observed
```

Installed-version probe (read-only, no harness settings changed): the DeepSeek
Harness CLI is installed as `dsh 0.1.5-rc.3` and its `dsh-session` package still
declares `session/created`, `session/event` and `session/disposed` with the
documented "a throwing `session/created` listener rolls back the attach" behaviour,
and `dsh-user-approval` still appends the `approval/asked` + `approval/decided`
audit pair the bridge correlates.

#### Remaining qualifications (not claimed as delivered)

1. **Packaged HUD lifecycle block not executed in this round.** The user's own running
   OVERSEER (pid 38960) owns `Local\OVERSEER.Hud.SingleInstance`, so a packaged
   second instance exits immediately by design and the lifecycle checks cannot pass;
   stopping the user's app was not authorized. Everything else in the packaged smoke
   ran (17/17). Re-run when the HUD is closed:
   `pwsh -NoProfile -File scripts/Test-PackagedRelease.ps1 -ArchivePath
   artifacts/final/release2/OVERSEER-v1-win-x64.zip -EvidenceDirectory
   artifacts/final/smoke-lifecycle` (from the repository root, with the workspace SDK
   on PATH).
2. **dsh is not attached live.** The observer plugin was not installed into the user's
   dsh profile (that is the opt-in step and would modify live harness settings), and no
   model-backed session was run. Contract tests plus the structural probe above are the
   evidence; live attach is not claimed.
3. **Claude Code CLI is not installed** (no `claude` on PATH, no
   `~/.claude/settings.json`, no npm-global package, no CLI binary in the installed
   package), so the bridge is synthetic-contract-tested only. Correcting an earlier
   over-broad claim: Claude **is** installed and running as the **desktop app
   2.9939.2.0** (MSIX `Claude_pzs8sxrjxfjjc`, "Cowork"; config under
   `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Roaming\Claude\`).
   Its bundle contains a Hooks surface with the same nine event names our mapping uses
   (PreToolUse, PostToolUse, Notification, UserPromptSubmit, SessionStart, SessionEnd,
   Stop, SubagentStop, PreCompact), but no configuration file of that app contains a
   `hooks` section and the documented Claude Code payload fields
   (`hook_event_name`, `transcript_path`) do not appear anywhere in its bundle, so
   its hook payload is not verified to match the bridge contract. The documented
   opt-in installer targets a Claude Code settings file (15 events, marker
   `overseer-claude-bridge`, preview-only unless `--apply`) and was exercised in
   preview mode against a temporary path; nothing in the live app was modified.
4. Physical checks not performed: real tray icon behaviour, mixed-DPI monitor
   combinations, and an audible speaker check. The offline baseline was verified
   silently through the packaged smoke and a real synthesized WAV.
5. A stored source enable/disable takes effect at the next composition (restart); the
   settings window discloses this.
6. One unrelated WPF hit-test test (`HudWindowTests.WholeSummaryCapturesDrag…`) was
   seen to fail once under full-suite load and passed in isolation and in subsequent
   full runs. No test was skipped, weakened or hidden.

### Latest Astra review and immediate fixes — inspected 26e642b

Scope: **no significant feature creep**. Four .NET projects/dependencies and frozen
documents are unchanged. Twenty commits since 37fd4fc implement the agreed monitor,
voice and project/strip work. Significant acceptance gaps remain; repeated session
checkpoints do not constitute completion. The older Current summary was stale and
has been corrected. No merge or new plan is warranted.

Immediate fixes made and tested by Astra:
- Composite excludes disabled adapters, accounts for enabled-but-unreported sources,
  rejects older source publications, serializes delivery, and stops replaying cached
  transitions on peer updates/reconcile. Transition retention is bounded. Global
  completion is derived from the full task set and a fresh completion, never forwarded
  from one source while another works.
- Claude discovery/reader now use its shipped `spool/claude/<id>` default; dsh keeps
  `bridges/dsh/<id>`. This corrects the incompatible default directory, not custom
  spool configuration (still pending settings work).
- Bridge forwards fresh attention edges (previously discarded); retained events at
  attach remain silent. Stale/incomplete lanes lower coverage. UTF-8 corruption no
  longer changes byte offsets, and task-number hashing cannot throw on int.MinValue.
  Poll delay now observes disposal cancellation.
- Bridge task first-seen/state/turn/completion clocks remain stable across action
  changes. Source matching includes harness ID. Non-Codex tasks cannot open Codex URIs.
- Shared project-resolver cache access is synchronized across adapter polling threads.

Evidence: baseline build and 555 .NET tests passed; final build 0 warnings/errors,
**564/564** .NET tests (nine new regressions, existing composite assertions corrected
to count actual deliveries), **48/48** producer bridge tests, diff check passed.
Inspected existing `artifacts/ux-strips6/fixtures/dark-expanded.png`; it shows project
headers and row strips but also the old dominant count graph, no strip legend/interval
detail, and a Codex-only footer. This is prior render evidence, not a new live smoke.
Running app/shortcut and live harness settings were not changed or repackaged.

Remaining ordered work for the fresh DeepSeek session:
1. **Bridge state/recovery correctness (MAJOR).** Consume and qualify real producer
   current-state snapshots, including source-only records; current.json is not read.
   FirstAttach never clears, so complete coverage cannot recover. Do not fix by simply
   clearing flags: define authoritative resynchronization and epoch/drop recovery.
   Preserve task/action evidence separately from host heartbeat (heartbeats currently
   refresh LastObservedAt and category). Unknown turn-end outcomes must not default to
   completed. Verify correlated late events, concurrent calls/requests, replay/rotation,
   clock skew and same-size file replacement using actual producer outputs as fixtures.
   Current timestamp cutoff is a conservative attach guard, not a full replay protocol.
2. **Composite/source budgets (MAJOR).** Shared aggregate I/O/source limits are not
   implemented; discovery is unbounded and happens only at startup. Source freshness
   and capabilities need explicit aggregation (currently last healthy capability set
   wins and composite health timestamps are fresh wall time). No fresh peer may make
   a dead producer healthy. Add bounded discovery/configured roots/enablement and
   injectable clock/behavioral tests; retain the fixes above.
3. **Voice lifecycle and settings (MAJOR).** Baseline provider exists, but default is
   still pack and settings/preview do not fully select/use speech. Preserve existing
   preferences while giving new configs the agreed baseline default. SAPI Speak is
   synchronous, cancellation is checked only after joining, and timed-out workers/temp
   WAVs can survive; bound worker count, cancellation and worker-owned cleanup. Test
   engine hangs, repeated cancellation and real offline preview. Remove unrequested
   cross-provider fallback: composition still wraps TTS in pack fallback despite the
   handoff's selected-provider rule. Keep custom recordings and mute semantics.
4. **Complete the project/HUD presentation.** Current project headers are not yet the
   expandable project surface/settings requested. Strip transport retains only Category,
   losing inferred/partial/mixed evidence and interval detail. Preserve those properties
   through bins/tooltips/keyboard semantics; add legend and remove the competing dominant
   count graph per handoff. Avoid recalculating whole-history projections per task on
   every UI tick. Complete close button/header/menu and size/scale matrix; fix the
   Codex-only footer/navigation wording without pretending unsupported routes work.
5. **Release gates.** New renders across required themes/sizes, baseline voice and
   actual available harness qualification, clean-source publish, packaged lifecycle/
   second-process/Snap/focus smoke. Diagnose unavailable environments explicitly and
   finish independent work; do not present synthetic-only coverage as live support.

Continue the original goal, fixing these before polishing fixture palettes. Full
completion is not certified by passing unit tests. README/CHANGELOG/ledger must reflect
the delivered state. Commit logical changes, no merge/push/history rewrite. The repo
had a clean worktree at review start, no remote; `.agent/HANDOFF.md` remains ignored.

### Astra continuation review — b06da76 (2026-09-24)

**CHANGES REQUIRED.** Six new commits since 3038bba, clean worktree at review start.
The restore call is now wired in App startup; the added signal test is an in-process
named-event test, not a packaged second-process test. Codex workspace roots/project
attribution are now wired through composition/reducer. The path normalization fixes
and bounded metadata read are substantive improvements. dsh atomic-current publication
passed in this review's combined bridge run. These advances do not finish the goal.

Remaining/new findings, in execution order:

1. **MAJOR — recorder still disconnected, with incorrect freshness semantics.**
   Source search finds no runtime caller of TaskActivityRecorder.Record and no runtime
   TaskActivityHistory instance. Its Record method uses snapshot.GeneratedAt for every
   task; a refreshed aggregate snapshot would refresh an unchanged old action. Preserve
   source/task action observation times and record before UI coalescing. ClassifyWorking
   returns plan updates with IsMarker=false despite its point-observation comment;
   repeated terminal snapshots also append duplicate markers. Add tests for a healthy
   A/stale B, repeated snapshots, one-time completion, and plan-update markers before wiring.
2. **MAJOR — path traversal protection remains incomplete.** FindRepositoryRoot checks
   only the current leaf for a reparse point before accessing its .git; an ancestor
   junction can already have redirected that access. The .git file and parsed gitdir/
   commondir targets are not checked equivalently before reads (including an absolute
   UNC gitdir). Validate every metadata target and relevant ancestors before probing;
   no UNC/POSIX metadata I/O. Relative gitdir is currently resolved against the .git
   file path rather than its containing worktree directory; add a real relative-gitdir
   fixture and verify grouping against Git's layout.
3. **MAJOR — bridge tests still not reliably green.** Fresh combined run: dsh 29/29,
   Claude 18/19. bridge.concurrency.test.mjs:71 expected 9 published records, found 8;
   all helper exits were zero. This demonstrates a missing record under the test's
   concurrency load, not proven non-monotonic sequencing. Diagnose lock/deadline/drop
   accounting and test assumptions without masking drops. dsh retry uses a Date.now
   busy loop on the host event loop; replace with bounded scheduled retry that preserves
   dirty state without blocking agent execution. Test transient failure deterministically.
4. **MAJOR — original product work remains absent.** Composite monitor/shared budgets,
   desktop dsh/Claude ingestion, per-source health/notifications, project rows/settings,
   activity strips, baseline voice, and visible close/header/sizing refinements remain.
   Complete them within the same goal, not a new sequence of human-managed plans.
5. **MAJOR — final qualification remains absent.** No new package/smoke/renders/live
   multi-harness/voice evidence. Named-event tests use the production event name and
   can compete with a real running HUD; isolate test event names and add the requested
   actual startup/second-process lifecycle check, including the listener-startup race.

Review evidence: fresh Release build 0 warnings/errors, .NET 506/506; bridge 47/48 as
above; diff check passed. No package/app/shortcut/live harness settings changed.
Git: ux/first-use at b06da76, six new commits (14 since d869db6); no configured remote.
This review is documentation-only and adds an uncommitted ledger edit. Earlier entries
are historical evidence; runtime recording is NOT delivered merely by adding the mapper.

Continuation: fix items 1–3 and test isolation while finishing item 4; perform item 5,
update truthful delivery claims, then return the entire goal for Astra review. Internal
commits/batches are fine; stopping after each batch is not completion. Stop only for a
concrete blocker, recording it and continuing independent work under the existing handoff.

### Astra review — 2026-09-24

Verdict: **CHANGES REQUIRED**. This is an incomplete implementation, not a release
candidate. Review compared the packet with `d869db6..3038bba`, inspected changed
composition/lifecycle, resolver and bridge paths, and reran build/tests. The broader
`main..HEAD` contains prior UX work; this partial review does not certify that full
52-commit history or live harness integration.

- **MAJOR — essential features are disconnected or absent.** No composite monitor,
  desktop bridge consumers, retained Codex workspace root, project-first UI/activity
  strips, offline voice, or visible close/header refinement. New resolver/history/
  adapter contracts are not consumed by the runtime. Complete the original packet.
- **MAJOR — second-launch restore is not wired.** `App.xaml.cs` exits immediately
  when the instance guard is not owned; it never calls `ShowRequestSignal.TrySignal`.
  The test invokes `RequestShow` directly and cannot validate the real startup path.
  Wire the signal and add a real isolated second-process regression (including startup
  timing), then run the packaged lifecycle check. The earlier delivered claim is wrong.
- **MAJOR — project path normalization can merge or misresolve projects.**
  `ProjectResolver.Normalize` lowercases POSIX paths, accepts drive-relative `C:repo`,
  loses the leading UNC prefix during segment joining, and can pop the drive/root
  segment for `..`. Preserve POSIX case, reject drive-relative/device paths, preserve
  root boundaries, and never probe UNC/POSIX as local Windows filesystem paths.
  The real filesystem probe also follows reparse points and uses ReadToEnd after a
  length check; enforce the promised traversal and actual read-byte bounds. Add tests
  that assert probe calls as well as resulting project IDs before runtime integration.
- **MAJOR — bridge verification is not green.** Combined Node suite: 47/48 passed.
  dsh atomic-current test failed at spool.test.js:118: final marker 85, expected 97.
  Isolated rerun passed 1/1, so the failure is intermittent, not resolved. The reader
  observed no torn document; do not describe this as proven data corruption. Inspect
  transient replacement failure/retry and the test's flush-success assumption; preserve
  dirty state, prove eventual latest snapshot publication, and make the test deterministic.
- **MAJOR — no release evidence for this round.** No new clean-source package,
  packaged lifecycle smoke, new render matrix, audible baseline or live multi-harness
  qualification. Existing baseline results cannot qualify this implementation.

Fresh review evidence: Release build **0 warnings / 0 errors**; .NET tests
**486 passed / 0 failed / 0 skipped** (the earlier 483 count is stale). Initial
sandbox build could not write intermediates; the permitted rerun passed. Claude
bridge tests 19/19; dsh 28/29 in the combined 48-test run, followed by the one isolated
passing rerun described above. `git diff --check` passed. No product code was changed
by this review; no package or live harness setup was attempted.

Version control at review start: clean `ux/first-use`, HEAD `3038bba`, eight commits
since `d869db6`. `main` remains `663caf5`, an ancestor 52 commits behind HEAD. No remote
or tracking upstream is configured, so these commits are local, not remotely backed up.
Frozen documents are unchanged from this round's base. The handoff is intentionally
ignored and is not carried by Git. Review documentation is a new uncommitted edit.

### DeepSeek continuation — 2026-09-24 (response to review)

Findings addressed on the same branch, each with behavioral tests:

- **Second-launch restore (MAJOR, was a wrong claim).** `App.xaml.cs` now calls
  `ShowRequestSignal.TrySignal()` before exiting a non-owner launch. The type is
  public and `ShowRequestSignalTests` exercises the real named-event round trip
  (repeated delivery, disposal). The packaged second-instance lifecycle check is
  still pending a package run.
- **Path-resolution regressions (MAJOR, fixed).** POSIX paths keep their case and
  compare case-sensitively; drive-relative (`C:repo`), bare/incomplete UNC and
  device paths are rejected; UNC server/share boundaries are preserved; `..`
  can no longer pop a drive or share root. The filesystem probe now enforces the
  byte bound in the read itself, refuses reparse-point ancestors instead of
  following them, and is never called for UNC or POSIX roots. New tests assert
  both probe calls and resulting project ids (12 cases).
- **Intermittent bridge test (MAJOR, fixed).** `writeAtomic` now retries a
  transient Windows rename failure (EBUSY/EPERM/EACCES) with a bounded backoff and
  keeps the dirty flag when every attempt fails, so the newest staged snapshot is
  eventually published instead of being silently skipped. The atomic-current test
  now proves eventual latest publication rather than assuming one flush won.
  Evidence: 12 consecutive `node --test` runs in
  `integrations/deepseek-harness` → 29/29 pass, 0 failed runs (previously one
  intermittent failure in a combined run).
- **Disconnected features (MAJOR, partially addressed).** The runtime now retains
  `session_meta.cwd` as a normalized workspace root through
  `SessionIdentityReader` → `TrackedSessionFile` → `AgentStateReducer` →
  `AgentTask.WorkspaceRoot`, resolves the project with `ProjectResolver` at
  admission (explicit/mapping/git/workspace/Unassigned), and exposes
  `AgentTask.AssignedProject`. `HudComposition` and the monitor debug harness
  construct the resolver from the new additive `project` config section
  (`ProjectSettings`), and `TaskActivityRecorder` records producer-side activity
  intervals (working/thinking/planning/waiting/unknown/markers) from normalized
  snapshots with the snapshot timestamp. Still missing: the composite source,
  desktop bridge ingestion, the project-first UI/strips and the baseline voice.
- **Release evidence (MAJOR, unchanged).** No package, packaged smoke, render
  matrix, audible check or live harness qualification was produced in this
  continuation. The branch has no release evidence for this round.

Verification in this continuation: Release build **0 warnings / 0 errors**,
.NET tests **506 passed / 0 failed / 0 skipped**, `git diff --check` clean.
Committed as `4dde05f`, `95bb482`, `132dbaa`, `77ff661`, and the activity-recorder
commit that follows this entry.

Ordered continuation prompt for DeepSeek (same goal/branch, no new micro-plan):
1. Read the reviewed handoff and this review; preserve all existing commits/work.
2. Fix startup restore and path-resolution regressions with behavioral tests; diagnose
   the intermittent snapshot test without hiding it through retries.
3. Finish workspace propagation, composite source ownership/budgets, desktop bridge
   ingestion and notification freshness/dedupe; then wire project/history UI and settings.
4. Finish offline voice and header/close/sizing work; characterize the outstanding
   suspend test race. Keep user configuration and unavailable live integrations honest.
5. Run full .NET/bridge tests, render review and real lifecycle checks; commit source,
   publish clean, run package smoke, record exact hashes and live qualifications.
6. Correct README/CHANGELOG/ledger claims and leave one coherent review-ready branch.
   Do not merge, push, rewrite history or claim completion with these gates missing.

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

### DeepSeek continuation 2 — response to "Astra continuation review — b06da76"

Findings 1–3 and the named-event isolation part of finding 5 are closed on this branch,
each with tests or repeated runs:

1. **Recorder connected with correct freshness semantics.** `NotificationCoordinator.OnSnapshot`
   now owns a runtime `TaskActivityHistory` and records from the same pre-UI-coalescing
   snapshot as history and voice. Observations carry the task's own `LastActivityAt`
   clamped to the snapshot time, so a refreshed aggregate snapshot cannot renew an
   unchanged old action (regression: the same task republished 30 s later keeps its
   confirmed interval end). Terminal states and plan updates are point markers with a
   stable per-episode identity, so repeated terminal snapshots add no duplicate marker
   and plan mode never becomes planning duration (two regressions). Healthy-A/stale-B
   separation is covered by `TaskActivityHistoryTests.AStaleMemberShowsKnownWorkPlusPartialCoverage`.
2. **Traversal protection is now complete.** The whole ancestor chain is checked for
   reparse points before the first metadata access; the `.git` target and the parsed
   `commondir` are validated and reparse-checked before any read; UNC/POSIX metadata
   targets never reach I/O; a relative `gitdir` resolves against the directory
   containing the `.git` file, with a fixture matching Git's worktree layout
   (`.git/worktrees/<name>/commondir` → `../..`).
3. **Bridge verification is green and honest.** The Claude failure was a real accounting
   bug: every non-ok outcome that was not a lock timeout was mislabeled
   `droppedOversizeRecord`, so a genuine write failure disappeared from the totals.
   A `droppedWriteFailed` counter now exists, the append loop cannot spin on a
   zero-progress write, and the concurrency tests assert landed records + counted drops
   = writers with per-session 1..N ordering for the landed ones. dsh no longer
   busy-waits: a transient rename failure keeps the document dirty and schedules a
   bounded retry. Evidence: dsh 8/8 runs 29/29 pass; Claude 10/10 runs 19/19 pass.
4. **Named events are isolated and the startup race is tested.** The event name is
   scoped by data directory (`ShowRequestSignal.EventNameFor`), the app passes that
   scope in both roles, tests use unique scopes, and a regression proves a signal sent
   before any listener is not queued for a later one.

**Finding 4 (product scope) and the package/live half of finding 5 remain open.** No
composite monitor, desktop dsh/Claude ingestion, cross-source notification policy,
project-first HUD/settings, activity strips, baseline voice, or close/header/sizing
refinement exists yet; no package, packaged lifecycle smoke, render matrix, audible
check or live harness qualification was produced. The running app, shortcut and live
harness settings were not touched.

Verification for this continuation: Release build **0 warnings / 0 errors**, .NET tests
**509 passed / 0 failed / 0 skipped**, dsh 29/29 and Claude 19/19 across repeated runs,
`git diff --check` clean.

### DeepSeek session checkpoint 3 — composite monitoring wired (2026-09-24)

**Session checkpoint, not a project blocker.** Product work resumed at `37fd4fc` and the
first two items of the packet are now implemented and wired into composition.

Delivered in this session (all committed):

- `Core/Monitoring/Bridge/HarnessBridgeRecord.cs` — the frozen bridge envelope parser
  (schema 1, exact keys, bounded sizes, unknown/malformed/unsupported outcomes).
- `Core/Monitoring/Bridge/HarnessBridgeSession.cs` — per-source folding with
  per-session sequence/epoch validation, hydration silence, correlated approvals/inputs,
  late-tool-start rejection after turn end, aborted-turn silence, error vs tool failure,
  plan mode as a point marker, and honest coverage: sequence gaps, producer drops and
  first-attach (pre-attach history unknown) all mark coverage incomplete instead of
  looking like healthy work.
- `App/Monitoring/BridgeMonitorAdapter.cs` — bounded incremental spool reader
  (`<data>/bridges/<harness>/<instance>/events.jsonl`, 1 MiB/pass, partial trailing
  line left for the next pass, rotation detected) plus producer health drops
  (`dropped*`/`overflow*` counters) converted into reduced coverage, and task
  projection onto the shared contract with project resolution.
- `App/Monitoring/CompositeMonitorSource.cs` — `CodexMonitorAdapter` over the existing
  Codex monitor and `CompositeMonitorSource` merging every source's latest snapshot:
  source-local health/reasons (`harness:reason`), counts that are only complete when
  every contributing source is complete (partial lower bounds otherwise, unknown when
  nothing is readable), transitions forwarded once, and global completion suppressed
  while any source's coverage is incomplete. A failing source can neither erase another
  source's tasks nor refresh its evidence.
- `App/Monitoring/HarnessSources.cs` + `HudComposition` — the HUD monitor is now the
  composite; a bridge source exists only when its spool directory exists (installed =
  present, never a disabled phantom), and `"disabledHarnessSources"` can exclude one.
- Startup race resolved (not merely tested): the show listener starts in the controller
  constructor, and a request that arrives before the window exists is queued and
  honoured as soon as the window is created.

Verification: Release build **0 warnings / 0 errors**; .NET tests **531 passed /
0 failed / 0 skipped**, including 22 new bridge/composite regressions:

- `HarnessBridgeTests` (12): envelope acceptance and harness-qualified identity;
  precise rejection reasons (empty, malformed JSON, non-object, missing schema,
  unsupported schema, unknown kind, missing timestamp, oversize); privacy sentinels
  (prompt/toolOutput/transcript keys never reach the record); hydrated attach is silent
  and marks pre-attach coverage unknown; sequence duplicates/out-of-order are typed and a
  gap raises coverage; aborted turn silent vs error turn raising attention; a late tool
  start cannot reopen a completed turn; approval correlation and resolution; plan mode as
  a point marker; producer drops lowering coverage; expired lease aging the task; a new
  producer epoch resetting ordering and marking the gap.
- `CompositeMonitorTests` (5): complete sources give known counts and each transition is
  forwarded once; a failing source keeps healthy work visible, forces partial counts and
  leaves the source set degraded (never a healthy zero); global completion is suppressed
  while any source is incomplete and appears once every source is complete; absent bridge
  spools are not phantom sources and a configured source can be disabled; the spool reader
  consumes only complete new lines, defers a partial trailing record, and reports producer
  drops as coverage evidence.

Cross-source notification verification is complete (`CrossSourceNotificationTests`, 3
regressions): the same conversation id in Codex and dsh produces two distinct
notifications instead of one collapsed cue; a degraded, partially covered composite
snapshot (one peer source failing) still notifies healthy work exactly once; and
repeated hydration-only publications speak nothing while a later explicit condition in
the same bridge session speaks exactly once.

Next executable step: the project-first HUD and settings surface plus the project/task
hourly activity strips (`ActivityStrips` is already wired into the coordinator), then
the offline baseline voice, the visible close button/header/menu/responsive sizing, and
finally renders + clean publish + packaged lifecycle smoke. Still absent: all UI work,
baseline voice, renders and any package/smoke evidence for this round; live Claude
qualification remains a disclosed gap (not installed).

Next executable steps, in order:
1. Cross-source notification verification (see above).
2. Project-first HUD/settings + project/task activity strips (`ActivityStrips` is
   already wired into the coordinator).
4. Offline baseline voice; visible close button/header/menu/responsive sizing.
5. Renders, clean publish, packaged lifecycle smoke (close now hides to tray), live dsh
   qualification and honest Claude absence disclosure.

Still absent: the UI work, baseline voice, renders, any package/smoke evidence for this
round, and live multi-harness qualification.

### DeepSeek session checkpoint 4 — offline baseline voice (2026-09-24)

Packet item 4 is implemented and committed as `6d0c40c`:

- `App/Voice/Speech/ISpeechSynthesisEngine.cs` — injectable engine boundary returning
  validated WAV bytes or a bounded failure code; the engine never plays audio.
- `App/Voice/Speech/SapiSpeechSynthesisEngine.cs` — Windows speech stream synthesis
  (`SAPI.SpVoice` + `SAPI.SpFileStream`) on a dedicated STA worker, explicit
  English (409) engine selection, 4 MiB byte cap, timeout plus abandonment of a stuck
  worker, COM release, temp-file cleanup, and a `speech-no-english-voice` /
  `speech-missing` unavailable result instead of a guessed voice.
- `App/Voice/Speech/WindowsSpeechVoiceProvider.cs` — maps each candidate to its fixed
  catalog script, synthesizes through the engine, re-validates the WAV with
  `VoicePackValidator`, caches at most nine clips, and reports `speech-*` codes. It
  implements only `IVoiceProvider`: playback stays in the existing
  `WavPlaybackService` path.
- `VoiceSettings` gained `VoiceProviderKind.Speech` with `speech`/`baseline`
  accepted names (unknown values still resolve to `pack`), and composition selects the
  baseline only when it is actually configured — no automatic cross-provider fallback.

Evidence: Release build **0 warnings / 0 errors**; .NET tests **546 passed / 0 failed /
0 skipped** (12 new `BaselineVoiceTests`: nine catalog scripts synthesize once and
cache, missing English voice is visible and silent, unvalidated audio is rejected,
engine failures are reported without throwing, the caller's cancellation token reaches
the engine, the provider cannot play audio, and provider-name migration never resets an
existing choice).

Live qualification of the engine path on this machine: one English voice is installed and
a bounded stream-synthesis probe produced a valid 78,500-byte RIFF/WAVE file. This is
API-level evidence only — no acoustic check was performed, and no audio was routed to the
speakers.

Next executable step: the project-first HUD (group `MonitoringSnapshot.Tasks` by
`AssignedProject` in `HudViewModel`, project header rows with harness badges and
per-project counts above their tasks, and hourly strips from
`NotificationCoordinator.ActivityStrips`), then the "Use baseline" control in the voice
settings surface, the visible close button/header/menu/responsive sizing, and finally
renders + clean publish + packaged lifecycle smoke. Still absent: any UI change, renders,
and all package/smoke evidence for this round.

### DeepSeek session checkpoint 5 — project-first HUD grouping (2026-09-24)

Packet item 3's grouping half is implemented and committed:

- `TaskRowViewModel` now carries a sortable `ProjectGroupKey`, the group label and a
  bounded group summary; the key orders attention (needs-human) → active → recent, with
  Unassigned forced last and a stable name tie-break, so groups never shuffle between
  ticks.
- `HudViewModel.ProjectGroups` groups the visible rows by `AgentTask.AssignedProject`.
  Counts, harness badges and coverage come from every monitored member of that project,
  not only the filtered rows, so a filtered view cannot claim full project health; a
  stale or provisional member appends `partial coverage` to the summary.
- `HudWindow.xaml` renders one header per project through a `CollectionViewSource` with
  a `PropertyGroupDescription` on the group key and a `GroupStyle.HeaderTemplate` showing
  the project name plus summary. The task row template and the single `Tasks` collection
  are unchanged, and labels/summaries never contain a local root path (asserted).

Evidence: Release build **0 warnings / 0 errors**; .NET tests **550 passed / 0 failed /
0 skipped** (4 new `ProjectViewTests`: attention-first ordering with Unassigned last,
one shared group key with counts and harness badges, partial-coverage disclosure for a
stale member, and no root path in any label or summary).

Next executable step: render inspection with `--hud-fixture` (and a project-bearing
synthetic scenario so a header is actually visible), then the hourly activity strips on
the project/task rows from `NotificationCoordinator.ActivityStrips`, the "Use baseline"
control in the voice settings surface, the visible close button/header/menu/responsive
sizing, and finally the clean publish plus packaged lifecycle smoke. Still absent: render
evidence, activity strips, the baseline settings control, the close/header/sizing
refinement, and all package/smoke evidence for this round.

### DeepSeek session checkpoint 6 — project headers rendered and refined (2026-09-24)

Render evidence and the follow-up fix from it:

- Synthetic fixtures are now project- and harness-bearing (`SyntheticHudScenarios` assigns
  Aurora / TCS pipeline / Webots harness / Unassigned projects and a dsh harness badge to
  two tasks), so a fixture render actually exercises grouping.
- `--hud-fixture` produced 26 renders (light/dark × collapsed, expanded, Now/Recent/
  Session, settings, light edges, unavailable, empty, and window sizes 280×720, 480×720,
  640×260, 900×700, 1400×900). Inspected `artifacts/ux-project/fixtures/dark-expanded.png`:
  project headers render above their tasks with counts ("Aurora 0 working · 2 needs you ·
  Codex", "TCS pipeline 2 working · 0 needs you · Codex") and a multi-harness group with
  the partial-coverage disclosure ("1 working · 1 needs you · dsh · partial"), with no
  clipping of rows or headers.
- Fix from that inspection: the header summary truncated at the minimum width
  ("partial covera…"), so the summary now uses short harness badges (Codex/dsh/Claude) and
  "partial"; the full text stays in the group tooltip.

Verification: Release build **0 warnings / 0 errors**; .NET tests **550 passed / 0 failed /
0 skipped** after the summary change; fixtures re-rendered from the same source.

Next executable step: the hourly activity strips on project and task rows from
`NotificationCoordinator.ActivityStrips` (the coordinator already records and exposes
them), then the "Use baseline" control in the voice settings surface, the visible close
button/header/menu/responsive sizing, and finally the clean publish plus packaged
lifecycle smoke. Still absent: activity strips, the baseline settings control, the
close/header/sizing refinement, and all package/smoke evidence for this round.

### DeepSeek session checkpoint 7 — project activity strips implemented (2026-09-24)

The project strip is implemented and wired end to end; render verification is still
outstanding and is stated as such.

- `ActivityTimelineGeometry` gained a binning overload for arbitrary interval
  sequences, so a single task lane renders with the same geometry as a project union.
- `App/Monitoring/CoordinatorActivityStripSource.cs` implements the new UI contract
  `IActivityStripSource` over `NotificationCoordinator.ActivityStrips`/`ActivityLanes`:
  one hour of history, at most 56 bins, recorded producer-side before UI coalescing.
- `HudApplicationController` supplies the source to the view model (only when a
  coordinator exists), and `HudViewModel` projects bins per row on the existing 1 s
  tick; `TaskRowViewModel.UpdateActivity` uses a per-bin signature so a strip only
  raises a change when it actually changed.
- `HudWindow.xaml` draws the project strip in the group header: one cell per bin with
  distinct brush/opacity per category (thinking, planning, working, waiting, error,
  complete, idle) and a muted low-opacity fill for unknown, which is deliberately never
  identical to confirmed idle.

Verification so far: Release build **0 warnings / 0 errors**; .NET tests **550 passed /
0 failed / 0 skipped**; fixture renders regenerated from this source. NOT yet verified:
no automated test covers the strip source or the bin projection, and the fixture render
path (`--hud-fixture`) does not construct a notification coordinator, so the rendered
PNGs cannot show a populated strip. The strip is exercised only through production
composition, so its visual result is unproven until either the fixture path builds a
coordinator or a live HUD run is inspected.

Next executable step: give the fixture render path the same coordinator the production
composition uses (so fixtures show strips), add strip tests (`CoordinatorActivityStripSource`
bins for a project and a task, empty history, unknown-vs-idle distinction), inspect a
render, then add the task-row strip, the "Use baseline" settings control, and the
close/header/sizing refinement before the publish + packaged smoke pass.

### DeepSeek session checkpoint 8 — strips rendered in fixtures (2026-09-24)

- `--hud-fixture` now builds the same muted `NotificationCoordinator` the production
  composition uses, records the synthetic snapshot producer-side and hands the view model
  the real `CoordinatorActivityStripSource`, so a fixture render exercises the strip
  wiring rather than a stub.
- Inspected `artifacts/ux-strips5/fixtures/dark-expanded.png`: every project header now
  carries a last-hour strip beneath its label/summary ("Aurora 0 working · 2 needs you ·
  Codex" and "Webots harness 1 working · 1 needs you · dsh · partial"), the strip spans the
  header width without clipping, and unobserved time renders as the muted low-opacity
  unknown fill — visibly distinct from the brighter known cells.

Verification: Release build **0 warnings / 0 errors**; .NET tests **550 passed / 0 failed /
0 skipped**; fixtures re-rendered from this source.

Stated gaps for this increment: the fixture only records a couple of known instants, so the
per-category palette is only partially exercised in the render; there is still no automated
test for `CoordinatorActivityStripSource` or `TaskRowViewModel.UpdateActivity`; and the
task-row strip is not implemented yet (only the project header strip).

Next executable step: automated strip tests (project and task bins, empty history,
unknown-versus-idle), a fixture that feeds staggered snapshots so several categories are
visible, then the task-row strip, the "Use baseline" settings control, and the
close/header/sizing refinement before the publish + packaged smoke pass.

### DeepSeek session checkpoint 9 — strip automation (2026-09-24)

The stated gap from checkpoint 8 is closed: `ActivityStripSourceTests` (5 regressions)
now cover the strip path end to end.

- A project with observed work yields bounded bins (1..56), contains the working
  category, contains unknown for unobserved time, and contains **no idle** — unknown
  coverage is never rendered as confirmed inactivity.
- A task lane renders its own bins, while a task that was never observed yields no strip
  at all rather than an invented one.
- With no history at all every project bin is unknown coverage.
- A lane observed twenty minutes earlier no longer claims working time in the recent
  tail (the ten-second freshness bound ages action evidence honestly).
- `TaskRowViewModel.UpdateActivity` raises a change only when the bins actually
  changed, so the per-second HUD tick does not churn the strip.
- Test detail worth recording: the bin window is half-open, so the tests project five
  seconds after the observation, exactly as the 1 s HUD tick does.

Verification: Release build **0 warnings / 0 errors**; .NET tests **555 passed / 0 failed /
0 skipped**.

Remaining for the strip work: the fixture records only a couple of known instants, so the
per-category palette is still only partially exercised visually; the task-row strip is not
implemented (project header strip only).

Next executable step: a fixture that feeds staggered snapshots so several categories are
visible in one render, then the task-row strip, the "Use baseline" settings control, and
the close/header/sizing refinement before the publish + packaged smoke pass.

### DeepSeek session checkpoint 10 — task-row strips (2026-09-24)

- The per-bin visual is now shared: `ActivityBinTemplate` and `ActivityBinsPanel` live in
  the window resources, and both the project header strip and the row strip use them, so
  the two cannot drift apart. The header's previous inline copy was removed (not left
  duplicated).
- `HudWindow.xaml` renders a last-hour strip inside each task row's subtitle column bound
  to `TaskRowViewModel.TaskBins`, which the 1 s tick already fills from the producer-side
  recording; row identity, turn timers and the task template's structure are unchanged.
- Inspected `artifacts/ux-strips6/fixtures/dark-expanded.png`: project header strips and
  task-row strips both render, the waiting and working observations appear as brighter
  known cells while unobserved time stays muted unknown, and nothing is clipped.

Verification: Release build **0 warnings / 0 errors**; .NET tests **555 passed / 0 failed /
0 skipped**; fixtures re-rendered from this source.

Still open for strips: the fixture records only a couple of known instants, so the
per-category palette is only partially exercised visually (a staggered-snapshot fixture is
the next step).

Next executable step: staggered-snapshot fixture for palette coverage, then the "Use
baseline" control in the voice settings surface, the visible close button/header/menu and
responsive sizing, and finally the clean publish + packaged lifecycle smoke +
README/CHANGELOG/ledger pass.

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
