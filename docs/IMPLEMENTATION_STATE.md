# OVERSEER Implementation State

## Current
Plan: complete pre-publication preparation, planned 2026-10-05.
Review branch: `plan/pre-publication`; reviewed main remains `fcabe00`.
Plan baseline main: `fcabe0033e6d5b04a10293e2f70de26625654dd6`.
Prior remediation baseline: `82e15e3`; remediation merged at `fcabe00`.
Status: **IN PROGRESS**; local preparation authorized 2026-10-05. Publication remains gated.
Input: [independent assessment](../.agent/REPO_STATE_ASSESSMENT.md), preserved unchanged.
Execution packet: ignored `.agent/HANDOFF.md`; prior packet preserved under
`artifacts/prepublication-planning-20261005/`. Previous remediation review was
performed in this chat, not by an independent second agent.

## Pre-publication plan
- Owner selected GitHub source/Releases **and a project website**; proposed GitHub Pages.
- Current runbook: [release readiness](RELEASE_READINESS.md), P1–P8 / H1–H16 / L1–L10.
- Audit source/history/privacy and dependencies; qualify actual desktop bridges;
  prepare current public docs/static site/SEO; build one final hash-bound candidate;
  collect hosted CI, owner hands-on and clean-machine results; then seek public approval.
- Local preflight found no site/public URL, no README screenshots, missing community/
  security/issue files, personal Windows path in history and split candidate evidence.
  Limited token-pattern matches were absent; full security/history scan is still required.
- Account/repository, history exposure, signing/unsigned and custom domain choices
  are needed during execution; no upload, deployment or announcement is authorized yet.
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
- Hosted CI and external release qualification are not claimed as complete.

## Completed implementation
- Main fast-forwarded by 146 commits from `663caf5` to `82e15e3`;
  `ux/first-use` retained and assessment snapshot committed in `ca9906b`.
- Added Windows CI: SDK from global.json, Node 24, Release/xUnit, both bridges,
  report/comparison controls and mocked upgrade-placement controls.
- Header menu reduced to eight actions; tray to six actions plus status.
  Both read actual shared menu state. Appearance/startup/reset/minimize are in settings.
- Removed unused count-chart control, geometry and projection. Bounded history,
  live task/project hour strips, unknown/partial semantics and voice remain.
- Archived the entire old ledger intact; current state stays below 200 lines.
  README consistently identifies RC8 and documents a standard Windows build.
- Added original-project MIT license and license scope; new archives include LICENSE,
  upstream MIT and runtime notices. Artwork and frozen specification were not changed.
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
Hosted CI is pending: the main repository has no configured Git remote.

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

## Desktop integration follow-up (2026-09-29)
- Installed DSH Desktop: 0.2.0-rc.2; bundled Node 24.18.1, protocol 4.
  Dedicated `.dsh/profiles/desktop` currently has no observer dependency/bundle.
  Installed session emit seams and list/header/sequence APIs match by source inspection.
  Use Desktop's Plugins page or its matching bundled CLI while fully quit; do not
  use a separately installed terminal DSH to manage this profile.
- Owner uses Claude Code in a desktop/editor app. Windows Claude package 2.9939.2.0
  is installed; this does not establish the embedded Code engine version.
  Official docs confirm shared Code hook settings and our command/args exec form.
  A standalone CLI on PATH is not required. WSL/SSH/cloud spool routing is unqualified.
- Both suites rechecked: DSH 30/30, Claude 29/29. READMEs now target these workflows.
  No live profile/settings were changed; actual session invocation is still untested.
- Standalone bridge directories carry MIT LICENSE files, and the DSH package declares
  MIT and includes its license. Publisher requires both files in new archives.

## Remaining release qualification
See [release readiness](RELEASE_READINESS.md) for protocols and revision-specific gates.
- Hosted Windows CI after a remote is configured.
- Clean Windows/browser download/SmartScreen, no-runtime and independent-machine checks.
- Real DSH Desktop / Claude Code bridge activation, interaction and teardown;
  hosts are installed but both bridges remain experimental.
- Physical Snap/drag/DPI, sleep/wake, monitor reconnect, audible/device and logon checks.
- Intentional activation and human tray interaction; excluded from this smoke run.
- Signing/distribution choice and public channel, explicitly deferred by owner.
- Codex approvals intentionally unsupported pending a separately qualified connection.

## Automated review — 2026-09-29
Verdict: **PASS WITH NONBLOCKING NOTES** against baseline main `82e15e3`.
- Reviewed menu/state synchronization, startup failure handling, settings bindings,
  tray fallback, chart removal, retained history/voice behavior and smoke evidence.
- Monitoring, voice policy, frozen spec, artwork bytes and upstream license are unchanged.
  All nine retired-branch commits remain reachable; old ledger is preserved byte-for-byte.
- Fresh build/tests and revision-specific full-duration packaged results support the change.
  Hosted CI, real desktop session behavior and physical checks remain unexecuted, explicit notes.
- Final follow-up: both bridge suites pass again; publisher and archive license checks pass.
  Claude installer preview applied nothing and left Windows user settings absent.

## Commits and next action
- `5393442`: menu/settings and dead-chart cleanup.
- `74bb348`: license, CI, build instructions and documentation/history.
- `d0186a8`, `9eb6220`: preserve original agent histories without stale source adoption.
- `2567976`: align the settings fixture with its rendered theme.
- `92a5473`: fix evidence generation for skipped visible checks.
- `5916556`: owner-confirmed MIT artwork scope, bridge licenses and desktop setup.

The complete pre-remediation ledger is in [IMPLEMENTATION_HISTORY.md](IMPLEMENTATION_HISTORY.md).
Next: execute P1–P7 from the current handoff as one review unit when authorized.
L1–L4 prepare the launch; L5 is owner approval of the final packet for public L6–L10.
This turn prepared the plan and local preflight; it did not rerun product tests.
