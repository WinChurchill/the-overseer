# OVERSEER changelog

## 1.0.0-rc.18 — private bounded drop-accounting fix

- Claude bridge 1.0.2 reserves health-accounting time after acquisition overhead.
  Previously a slow first attach plus a held writer lock could exhaust the entire
  remaining budget and drop a record without its counter. A deterministic fixture
  reproduces the loss before the fix and verifies accounting afterward.
- The fixed 250 ms write allowance and observe-only/fail-open behavior remain.
  RC17 emitted no package because its concurrency check still failed.

## 1.0.0-rc.17 — private Claude identity-race fix

- Claude bridge 1.0.1 publishes complete identity/epoch documents exclusively
  before peers can read them. A deterministic slow-write fixture reproduced the
  prior partial-document race and now passes; existing identities are preserved.
- Corrected project-site DSH question coverage and current HUD control labels.
- RC16 packaging was stopped by its concurrency/loss-accounting test; no RC16
  archive was emitted. Live host and final publication checks remain pending.

## 1.0.0-rc.16 — private bridge qualification controls

- Run bridge test files sequentially so unrelated fixtures do not compete with
  the fixed write budget; explicit concurrency/loss-accounting stress remains.
- Require each rebound Claude hook invocation to add its own record. Previously
  accumulated records could mask a later failed binding probe.
- RC15 bytes are preserved but failed the separate hosted Verify suite. RC16
  needs fresh exact-archive qualification; neither version is publicly released.

## 1.0.0-rc.15 — private automated qualification

- Added private CI packaging with archive, checksum and source manifest; no public
  release, tag or website deployment. Exact downloaded bytes are qualified locally.
- Added a hidden mixed-source endurance diagnostic, resource evidence and a
  qualification orchestrator that reuses existing package/upgrade suites.
- Prepared a clean-machine helper, expanded contributor/adapter/fixture guidance
  and added a PR template. Corrected DSH 1.1.0 question-coverage instructions.
- Actual native menu, host engines, clean OS, physical/audio and owner publication
  checks remain separate from synthetic automation. Codex approvals stay Unsupported.

## 1.0.0-rc.13 — private diagnostic-report cleanup

- Recognized Codex output-only function envelopes so live chats retain coverage
  and appear in Now. Genuine unknown records remain uncertain.
- Added one stable display-number sequence across Codex, DSH and Claude, shared
  by task rows, history and voice. Source-qualified identities remain unchanged.
- Aligned numbers and dividers, standardized status and age text, reduced repeated
  History labels, added a microphone state and light reason, clarified partial
  counts, improved dark metadata contrast and expired successful navigation messages.
- Split large shell, monitoring, bridge, reducer and view-model files without
  changing their state logic. Audited exception handling and removed exception
  messages from diagnostic reports. Exact-package diagnostics remain opt-in.
- Private qualification only; live-host, clean-machine, audible and physical checks
  remain required before publication.

## 1.0.0-rc.9 — private pre-publication qualification

- Prepared a static project website, descriptive README, privacy/support/security
  and contribution guides, sanitized theme screenshots and bug-report template.
- Added local website/link/metadata checks and publication guard controls; Pages
  deployment is manual, public-main only and requires explicit approval/configuration.
- Release payload now includes offline privacy, support and upgrade instructions.
  Corrected privacy wording and documented the DSH human-input limitation.
- Public, live-host, clean-machine, audible and physical-display qualification
  remains pending; this RC is unsigned and not published.


## Unreleased — repository assessment remediation

- Reduced the header menu to eight daily actions and the tray menu to six actions.
  Both read the same actual pin/visibility/mute state. Appearance, startup, reset
  and minimize controls remain available in Light & voice settings.
- Removed the unused count-chart renderer and projection; live task/project history
  strips, bounded history collection and voice status remain.
- Added Windows CI and a documented SDK/Node/PowerShell build path. Archived the
  previous execution ledger intact and reconciled the current RC8 documentation.
- Licensed original code, documentation and generated artwork under MIT, retained
  codex-monitor-hud's upstream MIT notices, and included licenses in new archives
  and standalone bridge assets. Artwork provenance is recorded from owner confirmation.
  Codex approval detection remains explicitly unsupported.
- Corrected setup guidance for DSH Desktop's dedicated plugin profile and Claude Code
  desktop/editor hooks. Real session qualification remains pending.
- Headless package smoke now initializes its focus observation collection, so deferred
  visible checks produce a complete evidence report instead of a strict-mode error.

## Unreleased — automated pre-user checks

- Added one sequential runner reporting all six pre-user test areas, with source and
  package identities, individual logs and explicit remaining manual qualification.
- Packaged integration now exercises partial answers, full answers while working,
  and a new question after an answer. Source degradation must show real limited
  coverage; the previous predicate incorrectly accepted a healthy `live` source.
- Package lifecycle tests can defer only intentional activation while keeping other
  checks on a secondary display. Scaling probes clean up their own hung process.
- Report controls reject missing, failed and unknown evidence while preserving
  deferred checks and manual qualification gaps. Private RC8 passed the automated
  six-area run; detailed counts and remaining release checks are in IMPLEMENTATION_STATE.

## Unreleased — Codex question-answer correlation

- Answered asynchronous questions now clear their own attention flag during the
  active turn. Partial answers keep unanswered questions visible; unrelated output,
  quoted envelopes, stale replies and duplicate answers cannot dismiss other work.
- Only bounded question counts, call IDs and question indices are retained. Question
  and answer text remain absent from normalized state, logs and notifications.
- Codex approval detection remains unavailable through the current rollout source.
  Diagnostics now describe that source limitation rather than asserting that Codex
  exposes no approval events through any interface.

## 1.0.0-rc.7 — review corrections

Internal candidate built from clean source `5aba6c2`. All five local findings closed:
704 .NET tests, Claude 29/29, dsh 30/30; extracted composition 42/42, integration 14/14,
silent voice smoke, and real RC6 upgrade/rollback 25/25 in both HUD and Window modes.
External qualification remains open; this candidate has not replaced the live app.

- Composition diagnostics now require an authorized display before creating windows,
  seed both window modes in every scenario, and assert the resulting bounds.
- Restarts use production adapter discovery/construction. Added applied-HUD checks
  for missing, stale, unsupported, locked and removed sources, recovery and peer survival.
- Claude hook changes validate the bridge even when its path was previously installed;
  a missing file cannot bypass validation with `--no-verify`.
- Upgrade/rollback comparisons reject lost or changed window settings. Only valid save
  timestamps may vary; `-WindowView` checks resizable mode separately. Negative controls
  cover corruption and deletion without launching windows.
- Muting and combined voice-status updates notify the actual WPF failure bindings.

## 1.0.0-rc.6 — 2026-09-28 (composition verification, bridge install hardening, lifecycle qualification, UI contract fixes)

Source changes for internal candidate `1.0.0-rc.6` on `ux/first-use`. The archive is
`artifacts/rc6/OVERSEER-1.0.0-rc.6-win-x64.zip`, built from source `9f805c8` with a clean tree.
It is an internal candidate, not a published release: the qualification status at the end of this
section records exactly which gates were exercised and which remain external.

- **Normal-composition verification.** New finite mode `--hud-composition-check` drives the real
  `HudComposition` graph — startup configuration loading, harness discovery, provider selection and
  controller construction — and asserts the state that reaches the real HUD view model: an empty
  first run (muted, `Monitoring offline`, no invented rows, clean shutdown), no harness installed,
  valid mixed sources, a bridge added while running (picked up only at the next start, as
  documented) and an existing configuration staying authoritative. It isolates itself under the
  evidence root by default and refuses an explicit request for the live data directory, because it
  composes a controller that saves state on shutdown. Recorded: 26/26 checks,
  `artifacts/composition-check-noarg/run-20260928T014636Z-a097864a/composition-report.json`.
- **One composition seam.** The integration check's invented producers moved out of
  `HudIntegrationCheckRunner` into `SyntheticProducers`, and `HudComposition` gained the seam that
  lets a diagnostic compose the same dependency graph the app uses instead of building its own.
- **The Claude installer refuses a path it has not executed.** `install.mjs --apply` now runs the
  hook command it is about to record once, with a synthetic payload against a throwaway spool root,
  and refuses to write (exit 2, no backup, no change) when the recorded path is missing, is not a
  runnable file, exits non-zero or publishes no record. The probe only runs when the run would
  record a path the settings file does not already point at, so re-applying an unchanged install
  stays idempotent. New `lib/verify.mjs` owns the probe; `bridge.mjs` never imports it.
  `test/installer.binding.test.mjs` covers a missing payload, a non-runnable file, a successful
  rebind to a new extraction whose generated command really publishes a record, and a refused
  rebind that leaves the old settings intact.
- **Rebinding is the documented recovery path.** The integration docs now state the per-host truth:
  `dsh plugin add "<path>"` is a `pnpm` forwarder, so a bare directory path becomes a `link:`
  dependency (a directory junction on Windows) rather than a copy, and it declares no dry-run flag;
  deleting the extracted directory makes `dsh` skip the bundle or the recorded Claude hook dangle;
  recovery is `install.mjs --bridge="<new path>" --apply` on one side and the host's own
  `dsh plugin` command on the other. `integrations/README.md` and both integration READMEs were
  corrected to say this, including that the extracted integration directory must be kept.
- **UI contract fixes** (audited carryover defects, regressions in
  `tests/TheOverseer.Tests/UiContractCleanupTests.cs`). The Recent-hours selector is bound to
  `SelectedRecentHours`, which never notified, so a window chosen from the header menu left the
  selector showing the previous value while the list already used the new one. `LeftAccentVisible`
  was raised by nothing, so the left state accent never followed the light toggle in either
  direction. A task history strip could be disclosed with every project history closed yet render
  without the shared hour axis and legend, which now appear for either disclosure. `VoiceStatusLine`
  was computed but rendered nowhere, so with notifications explicitly enabled a failed provider
  changed nothing on screen; the health detail now carries a failure-only line that is absent while
  muted or healthy. Status feedback (tray, startup, navigation and "settings or position could not
  be saved") reached `FooterHint`, which no surface rendered; it now has a subdued footer at the end
  of the scrolled content, hidden entirely when there is nothing to report.
- **Upgrade and rollback qualification.** `scripts/Test-ReleaseUpgrade.ps1` now seeds a complete
  previous-release state and compares 89 JSON paths semantically (monitor roots and limits, logging
  fields, HUD settings and window modes, voice provider/endpoint/events/clips, project mappings and
  aliases, disabled sources, saved placement), plus the spool documents (`events.jsonl` records,
  `current.json`, `health.json`) and recordings by SHA-256.
- **The rollback boundary is exercised, not asserted.** The previous build runs against a copy of
  the data directory the candidate wrote and never reuses another run's directory in place, and the
  pre-update backup route is verified by damaging the candidate-written copy (`config.json` and
  `bridges/` removed), restoring the backup over it and comparing the result with the pre-update
  data.
- **Update targets are checked through production code.** A probe drives the real
  `StartupRegistration` helper against a file-backed fake registry store (the user's `HKCU\…\Run`
  value is never read or written) and checks the quoted candidate executable with
  `--start-in-tray`, that `dotnet.exe` is rejected, that a moved package's recorded path is reported
  stale, and that the opt-in survives; a copied `.lnk` in the evidence directory must follow the
  candidate directory.
- **The integration hook reference is recorded honestly.** The same check writes an integration
  record and reports that the shipped Claude installer stores an absolute path outside the data
  directory, so extracting a new version into a new directory leaves that path pointing at the old
  one; it fails when the candidate does not ship the same relative integration path. Rebind after
  upgrading.
- **The mocked placement harness follows the three-launch contract.**
  `scripts/Test-ReleaseUpgradePlacement.ps1` mocks only display discovery and process launches and
  now asserts the launcher names, executables and arguments, the recorded rollback, the verified
  backup/restore and the startup/shortcut evidence fields. Its recorded run
  (`artifacts/upgrade-placement-tests/33c099b52c714915b2c4282cec15b0aa/`) passes 25/25 checks in the
  secondary-display and explicit-primary cases; the no-secondary case is deferred and fails
  qualification by design. The comparison helpers themselves are unit-exercised (71 assertions,
  including 43 single-field mutations reported at their exact JSON path), and the seeded schema was
  probe-verified (9 voice events, 5 clips, 3 roots/harness roots/disabled sources/mappings/aliases
  loaded and round-tripped).
- **Lifecycle qualification is now safe to run.** `Test-PackagedRelease.ps1` gained
  `-SkipVisibleChecks` and `-VisibleCheckMonitor` plus one shared deferral reason: a headless run
  records every windowed check as skipped-with-a-reason and exits `1` instead of silently dropping
  it, and a requested display is honoured only when it exists and is not the primary one.
  `scripts/Test-WindowScaling.ps1` was rewritten to choose a target display explicitly, seed the
  isolated run's saved placement for it, assert that the window really opened there, and skip the
  primary display in the per-monitor loop unless `-IncludePrimaryDisplay` is passed.
- **Packaged graceful Exit is proven through the normal composition.** A new
  `hud-live-graceful-exit` check launches the app with the tray disabled, where WM_CLOSE runs the
  unconditional orderly shutdown, and requires exit code 0 with the placement flushed and no forced
  kill; the forced kill in the tray run is now documented as timeout cleanup rather than as exit
  evidence.
- **Two real defects found while making those checks runnable.** Placement seeding wrote only
  `monitorDevice`/`offsetXDips`, which the collapsed HUD reads, while the window view places by
  `WindowMonitor`/`WindowX`, so a window-view run silently opened on the primary display; both modes
  are seeded now and the scaling check fails loudly when the window is not on its target. The
  normal-composition check also fell back to the user's live data directory when no
  `--data-directory` was given.
- **Dead UI code removed after the carryover audit.** `CycleRecentWindowCommand` (the superseded
  cycling gesture, never bound in XAML), `SetHealthBrush` (no caller, and it fought the XAML style
  triggers), a no-op `MinHeight` ternary and an empty resource dictionary were deleted; no XAML
  binding target was missing and no handler was wired twice. All 46 fixture renders are recorded as
  byte-identical afterwards.
- **Documentation.** New [docs/UPGRADE_AND_ROLLBACK.md](docs/UPGRADE_AND_ROLLBACK.md) states the
  supported upgrade path, both tested rollback routes, the exit codes and what is not verified; the
  clean-machine qualification protocol was added to `docs/RELEASE_READINESS.md` and is explicitly
  **not executed**.
- **Qualification status.** Measured on this machine for the exact rc.6 archive: packaged smoke
  **44/44 with no skipped block** (two displays; every windowed check on `\\.\DISPLAY2`), the
  packaged composition check **26/26** and production-path integration check **14/14** from inside
  the extracted archive, the real `rc.5 → rc.6` upgrade comparison **25/25** including a
  candidate→previous rollback run and a pre-update backup/restore boundary, three mocked upgrade
  placement cases **3/3**, Release build **0 warnings / 0 errors**, **695** .NET tests, and the
  shipped bridge suites **dsh 30/30** and **Claude 25/25**.
  Still open, and not claimed: browser download on a clean machine with no .NET runtime, a clean
  VM or independent machine, live per-version `dsh`/Claude-host evidence (both integrations stay
  experimental), signing/distribution/terms decisions, and physical keyboard/Snap/DPI/sleep-wake
  and audible-playback checks. `docs/RELEASE_READINESS.md` tracks each with its exact missing
  prerequisite.

## Unreleased — 2026-09-27 (review corrections)

- Explicit show and user input now end the startup focus guard immediately; timeout
  and shutdown clean up only its temporary no-activate flag, preserving other styles.
- Source health keeps missing evidence unknown and uses the source's own fallback
  observation time, so unchanged sources cannot appear freshly read by a peer update.
- Upgrade qualification now exits with deferred evidence before launching any window
  when no secondary display exists and primary-display testing was not enabled.

## Unreleased — 2026-09-26 (usable release candidate: simplified HUD, packaged bridges, candidate verification)

Internal release candidate `1.0.0-rc.1` on `ux/first-use`. It is **not** a public
release: clean-machine, live-harness and signing gates are still open (see the
qualification gaps in `docs/IMPLEMENTATION_STATE.md`).

- **Simplified HUD.** One attention-first summary whose halves open the matching
  filter (*All* is always visible as the reset), one clickable health control with
  per-source detail, an explicit Recent-hours choice, and per-project/per-task history
  disclosure that keeps the shared hour axis and legend inside the disclosed area.
  Settled projects start collapsed; a new attention episode reveals its project once
  without focus. Decorative linework, the duplicate status headline, the constant
  voice sentence and the permanent instruction line are gone. Body text is 13 DIP,
  secondary text 12 DIP, and the collapsed HUD is 300x96 DIP so nothing is clipped.
- **Source-aware health wording.** Composite monitoring prefixes every reason with its
  harness, so the HUD matches reason codes with or without that prefix: a degraded
  `dsh` or Claude source is no longer displayed as healthy, and the empty state no
  longer claims a Codex-only view.
- **One instance scope.** The single-instance mutex and the "show the running HUD"
  event now derive from one identity: the Windows user plus the canonical data
  directory. An isolated run owns its own slot instead of losing the mutex and
  signalling the wrong scope, which is also what makes an isolated packaged lifecycle
  check possible while your own HUD keeps running.
- **Production-path candidate check.** `--hud-integration-check` invents Codex and
  bridge producer files and drives them through the real readers, composite and
  controller, asserting the applied HUD state: working → input → answer → completion,
  asynchronous attention while working, honest per-source degradation with a surviving
  peer, no monitor-stopped latch, recovery on clean data and advancing freshness.
- **Bridges ship in the archive.** The dsh and Claude assets, notices, installers and
  their tests are included under `integrations/`, runnable from the extraction alone,
  with an index that states prerequisites, the data-directory spool location (so an app
  upgrade cannot move it), and that the host and OVERSEER must be restarted.
- **Release identity.** A numeric `FileVersion`/`AssemblyVersion` is now separate from
  the release identifier and source commit: `1.0.0-rc.1` ships as file version
  `1.0.0.0` with `1.0.0-rc.1+<commit>` in the product version, and the archive carries
  `release.json`. The publish validation compares the numeric version exactly instead
  of the unsuitable `FileVersion.StartsWith(Version)`.
- **Upgrade checks.** The packaged smoke verifies that a previous release's settings,
  project mapping, disabled source, custom recording, saved placement and
  data-directory spool survive, and `scripts/Test-ReleaseUpgrade.ps1` compares an older
  archive with the candidate on copies of one data directory.

## Unreleased — 2026-09-25 (monitor offline regression)

- Separate monitor startup from its lifetime. Successful composite initialization
  no longer makes the HUD latch offline and discard every subsequent snapshot.
- Observe Codex termination as a source-local failure while peer sources continue.
- Added a controller/composite regression covering startup, subsequent updates,
  source failure and shutdown. Release tests: 668 passed.

## Unreleased — 2026-09-25 (input prompts and scrolling)

- Recognize persisted Codex `request_user_input` and `request_user_input_async`
  tool calls as input requests using only tool identity and call correlation.
  Questions, options and answers are not inspected or retained. Blocking requests
  enter NeedsInput; asynchronous requests flag NeedsHuman while work continues.
- Clear a blocking request on its matching tool response. Do not mistake an async
  tool's immediate acknowledgement for an answered question. Async resolution still
  depends on an explicit correlated event or the existing turn boundary.
- Use one scroll surface below the pinned HUD controls at every window size,
  removing the nested short-window task scroller and its fixed 180-pixel height.
- Release build: 0 warnings/errors; 667 .NET tests passed, including input lifecycle,
  privacy, wheel routing and last-row reachability at short/tall sizes. Rendered 38
  synthetic fixtures and inspected representative short/dark and tall/light layouts.

## Unreleased — 2026-09-25 (Astra review and cleanup)

- Start all harness adapters without waiting for the Codex monitor to shut down.
- Read configured bridge folders, deduplicate aliases, retain disabled sources
  without consuming enabled slots, and keep discovery failures/caps visible in coverage.
- Apply shared byte/session limits to Codex and bridge consumers; count bridge
  health, fingerprints, rotations and snapshots in the same poll allowance.
  Small shares progress through partial records and snapshots without increasing the budget.
- Prevent corrupted snapshots from reusing an older healthy cache; preserve drop
  counts on malformed health data, guard counter overflow, and report incomplete rotation recovery.
- Expire silent adapters independently of peer updates, expire old empty-source
  snapshots, and prevent duplicate bridge starts or disposal of an in-use poll gate.
- Add 18 regressions covering runtime composition and adversarial input. Release
  build: 0 warnings/errors; 659 .NET tests and 51 bridge tests passed. This source
  revision has not yet been packaged or qualified against live harnesses.

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
