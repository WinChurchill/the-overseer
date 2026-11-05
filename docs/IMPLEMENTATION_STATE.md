# OVERSEER Implementation State

## Current
Plan: Local alpha qualification completed, 2026-11-02; secondary screen only.
Branch: `fix/detection-logic-cleanup`, base `63eff72`; alpha branch push authorized 2026-11-04.
Status: **LOCAL ALPHA QUALIFIED; EXTERNAL/HOST CHECKS AND PUBLICATION OPEN.**
- Existing work preserved; Winston authorized commit/push of this task branch only.
- Main merge, release/tag publication and repository visibility remain unchanged.
- Kokoro muted default, saved choices preserved; Windows synthesis passes, acoustic open.
- Actual normal HUD Codex→Kokoro input cue heard once; no Preview/helper used.
- Older async questions become unconfirmed after existing15m lease, IDs retained;
  fresh questions rearm and fresh work remains current. Dismissal is not observable.
- Claude desktop-only reader covers loaded app windows, bounded and nonactivating;
  dedicated titles/fallback IDs; cosmetic renames; two live Idle workers named.
- Codex agent-created/handoff chats included; genuine subagents/reviews excluded.
- Full normal-extraction VSTest930/930, no skips; build0 warnings/errors.
- Claude44/44, DSH40/40, fixed fixture drift and site85/13 controls pass.
- Capture passes after native initialization/serialization; secondary test opt-in.
- Short README retains both setup paths, larger screenshots and tester handoff.
- Low-integrity workspace explained native Claude/speech gap; normal install passes.
- Running alpha `.3`: names, reversible summary/divider drag and settings verified.
- Exact alpha `.3` smoke46/46, no deferrals; cold counter fix preserves250/60ms.
- Live Claude Code and DSH reply-only trials produced lifecycle events. Input/approval, clean-machine and physical checks remain open.
- Public matching-source release needs reviewed main integration and exact CI asset.

## Prior RC21 review baseline
- [PR #1](https://github.com/WinChurchill/the-overseer/pull/1) merged as `63eff72`;
  navigation exception fix `3767d4a` is absent from the older RC21 ZIP.
- Pre-merge hosted checks: build 0 warnings/errors, 778 .NET/39 DSH/33 Claude pass.
- Older RC21 source `c227dd9`; [package](https://github.com/WinChurchill/the-overseer/actions/runs/38212805343),
  SHA256 `AC9AAEDC540744AD19FE45136EA8BFC16DC9C0E9349B053A4133CFDEA64FF2EA`.
- Exact ZIP46 checks, integration17/composition42, upgrade/rollback25/25 per mode,
  synthetic hour14/14; source/hash-bound evidence remains in RELEASE_READINESS.
- Native RC21 lifecycle/input/theme probes and prior failures remain in
  `artifacts/prepublication/rc21-qualification/owner-handoff/` in the original worktree.
- Actual-host hour, independent machine and owner release checks remain open.

## Owner feedback — 2026-10-05
- RC9: missing selected pack caused silent Preview; actual offline English speech
  synthesis/validated WAV passes in isolated data. Use Windows speech explicitly;
  audible owner retest remains pending. Saved provider choices were preserved.
- DSH observer installed via Desktop bundled manager after owner confirmed full quit,
  with private profile backups. Relaunch now publishes fresh health, no drops/errors.
  Production discovery/reader accepts the live stream (21 records, one session);
  Adapter reads one idle task with complete current coverage and a late-attach history
  gap. Owner confirmed live approval/resume; RC11 question detection was Unsupported.
- Recent ComboBox face/popup/item states themed; standalone Desktop Work managed
  folders resolve to Independent chats, with mappings/Git taking precedence.
- An older app-copy question is superseded; the later live HUD check showed this
  chat without attention. RC12 says question pending; no answer was fabricated.
- Claude's 15 hooks emit work/completion records and its row is visible. Isolated
  execution/idempotence pass; real input/permission and independent-machine checks pending.
- RC12 shortcut uses separate writable test settings and real streams; saving verified.

## New DSH question review fix
- Observer 1.1.0 targets Desktop 0.2.0-rc.2 cached metadata, including timed continuation.
  Correlated replies and quiet restart recovery work; missing metadata reduces coverage.
- Native installed-code synthetic contract and real reader fixtures pass; DSH 39/39,
  Claude 29/29; Release 741 pass/0 fail/skip, recorded in owner-feedback/question-fix-tests.
- Linked observer updated after owner-confirmed full quit; real question retest pending.
  Codex approvals remain Unsupported; no publication/merge.
- Prior RC12 source `68c2d619`, package and owner kit preserved; see RELEASE_READINESS.

## RC13 cleanup and qualification
- Known Codex function-output envelope no longer makes live coverage uncertain;
  synthetic hydration/append and read-only live Now projection pass. Full H3 pending.
- Shared canonical display numbers cover task rows and attention/history/voice edges;
  cohesive partial files, UI polish and 34-catch audit complete; opt-in diagnostics kept.
- Clean source `c0ced607ff5ab9ab7df9e10f7c0ea13eff5cde67`; ZIP 76,915,148 bytes, 591 files/90 integrations.
  SHA-256 `45A9F3F2D91B65DCDC91F25A896EEE49FF29B9DE55F49EC4BFBA71DF7B0E27AA`.
- Clean build 0 warnings/errors; 749 .NET, DSH 39 and Claude 29 pass. Exact ZIP:
  42 pass/0 fail/3 activation deferrals; integration 17/17, composition 42/42, 48 renders.
- No focus takeover/546 samples; scaling and RC12 upgrade/rollback pass in both modes.
  Gitleaks 8.30.1 history (202 commits), source/payload clear; site 85/12 checks pass.
- Fresh RC13 folder/shortcut prepared with separate settings; RC12 not replaced.
  Owner kit binds 16 PENDING checks to RC13; RC12 kit archived. No scratch deletion.
- Same-chat review PASS WITH NONBLOCKING NOTES; not independent review/public approval.

## Prior RC11 execution results
- RC11 source `8b1d345`, ZIP/evidence and owner-feedback/rc11-owner-kit preserved.
- Release 727 tests; exact ZIP 42/0/3, bridges DSH 30/Claude 29; detailed evidence
  remains in RELEASE_READINESS. Prior resource probes do not satisfy live H15.

## Pre-publication plan
- Owner selected GitHub source/Releases **and a project website**; proposed GitHub Pages.
- Current runbook: [release readiness](RELEASE_READINESS.md), P1–P8 / H1–H16 / L1–L10.
- Audit source/history/privacy and dependencies; qualify actual desktop bridges;
  prepare current public docs/static site/SEO; build one final hash-bound candidate;
  collect hosted CI, owner hands-on and clean-machine results; then seek public approval.
- Initial preflight gaps in public docs, screenshots, site and scan evidence were
  addressed in RC9; private source/hosted CI are verified, public URLs remain pending.
- Private source upload/account/history scope are authorized; signing/public exposure
  and custom domain remain decisions. Public deployment/announcement is not authorized.
- Prior verification below belongs to its named revisions, not this new plan.

## Scope and decisions
- Owner selected MIT for original code/docs, including AGENTS.md and the V1 spec,
  and confirmed that all supplied images were generated for this work. Original
  artwork/reference images and derived icons are now included in project MIT.
  Provenance is recorded from owner confirmation; the earlier exclusions are superseded.
- Preserve upstream MIT notices for adapted codex-monitor-hud monitoring code.
  Initialization chat and existing source headers confirm actual code adaptation.
- Ryan Lab is recorded as website inspiration; no copied asset/code was identified.
- Owner will perform hands-on tests; clean-machine qualification remains a test.
  Publication, signing and channel choices are deferred until qualification.
- Codex approval detection stays explicitly **Unsupported**, by owner decision.
  Async question detection and correlated answer clearing remain supported.
- Hosted CI passed prior staging; final-byte/public qualification remains pending.

## Completed implementation
- Main advanced from `663caf5` to `82e15e3`; `ux/first-use` and assessment `ca9906b` retained.
- Added Windows CI: SDK from global.json, Node 24, Release/xUnit, both bridges,
  report/comparison controls and mocked upgrade-placement controls.
- Header menu reduced to eight actions; tray to six actions plus status.
  Both read actual shared menu state. Appearance/startup/reset/minimize are in settings.
- Removed unused count-chart control, geometry and projection. Bounded history,
  live task/project hour strips, unknown/partial semantics and voice remain.
- Archived the old ledger intact; current ledger stays at most 200 lines.
- Added project MIT/license scope; archives include project/upstream/runtime notices.
- Fixed the existing headless smoke-report error: focus observations are initialized
  even when visible lifecycle checks are skipped.

## Verification
Verified app/source: `25679762f2cd79b8824336c996897c60d94cc95e`.
Smoke-report fix: `92a5473`; subsequent follow-up changes are documentation, bridge
license files/package metadata and publisher license checks. App/test source is unchanged.

- Baseline: build 0 warnings/errors, 722 .NET tests, dsh 30/30, Claude 29/29.
- Fresh local clone: restore/build pass, 0 warnings/errors; **715 .NET tests pass,
  0 fail/skip**. Seven obsolete count-chart cases were removed.
- Both bridge suites pass sequentially: dsh 30/30, Claude 29/29.
- Report controls 7/7; comparison controls 55/55; mocked placement cases 3/3.
- 46 offscreen renders; both settings themes and smallest HUD layouts inspected.
- PowerShell script parsing, workflow YAML/structure and git diff checks pass.
- Private packaged integration 17/17 and composition 42/42 pass.
- Packaged smoke on DISPLAY2: **42 pass, 0 fail, 3 activation checks deferred**,
  full-duration run, live monitor readable; zero owned-foreground samples / 548.
  Evidence: private-windowed-smoke/run-20260929T211451Z-286b0cc5/ under the evidence root.
- Headless negative run writes evidence and exits 1: 20 executed checks pass,
  21 visible checks intentionally omitted and reported as failures; no skipped check
  is promoted to a pass. This verifies the repaired report path.

Evidence: `artifacts/remediation-verification/`, including logs, TRX, renders,
headless/windowed evidence and private-package manifest. Development failures remain.
Fresh-clone verification uses this machine's installed SDK and existing package cache
via environment/PATH; the cloned repository needs no adjacent ../.tools directory.
Hosted CI was pending for that historical run; private staging now has a verified pass.

## Private verification archive
`1.0.0-dev.remediation+2567976`, unsigned, clean source, 569 payload files,
75 integration assets; this is a local test artifact, not a replacement release candidate.
Path: `artifacts/remediation-verification/private-package/OVERSEER-remediation-verification-win-x64.zip`.
SHA-256: `719E6C3E21D5EB004E785127B8A3D0EC73CE8AC8662FD577D87C8E61EC0E627F`.
Project LICENSE, THIRD_PARTY_NOTICES and upstream MIT match source bytes exactly.

License/setup verification archive: `1.0.0-dev.desktop+5916556`, clean source,
571 payload files / 77 integration assets; no app/test source change from the revision above.
Path: `artifacts/remediation-verification/desktop-followup-package/OVERSEER-desktop-followup-win-x64.zip`.
SHA-256: `2CB2A075254173771CBAEAD2B25A3D30E08B1236218C1FF9D8FF22355F279CB6`.
All five project/upstream/bridge license files match source bytes; MIT/artwork scope and
DSH package license inclusion pass. This archive was not rerun through visible smoke.

## Designated packaged candidate (historical binary)
RC8: `1.0.0-rc.8+f14dc47`, unsigned; source `f14dc47`.
Path: `artifacts/rc8/OVERSEER-1.0.0-rc.8-win-x64.zip`.
SHA-256: `CB55897A6A75F4B5CBDF975D904DEDCE08347B8BE1F96C7793C726A22461BA93`.
Historical six-area evidence:
`artifacts/pre-user/run-20260928T065109Z-aab5b5d0/pre-user-report.json`.
Its binaries predate remediation; those results certify that revision only.
The user's live installation was not replaced and no public artifact was published.

## Repository topology
All nine agent/ui and agent/voice commits were verified patch-equivalent to main.
Original tips `fae8aae` and `fe2ff0c` remain reachable through history-only merges
`d0186a8` and `9eb6220` on this branch; those merges changed no source files.
Their worktrees and redundant branches were retired after clean-state checks.
All 393 qualification files were preserved and hash-verified under
`artifacts/retired-worktrees/agent-ui/artifacts` and `agent-voice/artifacts`;
full patch mappings and per-file hashes are in `artifacts/retired-worktrees/preservation.json`.
The merge gate has passed; unrelated historical branches are retained.

## Remaining release qualification
See [release readiness](RELEASE_READINESS.md) for protocols and revision-specific gates.
- RC21 bytes qualified; final1.0/signing need a new CI archive and qualification.
- Clean Windows/browser download/SmartScreen, no-runtime and independent-machine checks.
- Real DSH Desktop / Claude Code bridge activation, interaction and teardown;
  hosts are installed but both bridges remain experimental.
- Physical Snap/mixed DPI, sleep/wake, monitor reconnect, audible/device and logon checks.
- Activation and native header Exit pass; actual tray-menu interaction remains.
- Signing/distribution choice and public channel, explicitly deferred by owner.
- Codex approvals intentionally unsupported pending a separately qualified connection.

## Commits and next action
- Original remediation commit identities remain in IMPLEMENTATION_HISTORY and
  preserved local refs; GitHub normalization is documented in PRIVATE_GITHUB_STAGING.

The complete pre-remediation ledger is in [IMPLEMENTATION_HISTORY.md](IMPLEMENTATION_HISTORY.md).
Next: five owner work blocks; exact48-clause checklist has29 PASS,14 OWNER_ACTION,
2 NEEDS_ACCESS,1 intentionally Unsupported and2 preserved context failures.
Use the current owner packet, then seek concrete L5 publication approval.
Source merge state is recorded in PR #1. No public release has been approved.
