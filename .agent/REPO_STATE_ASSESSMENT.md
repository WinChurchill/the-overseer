# OVERSEER — independent repository state assessment

| Field | Value |
| --- | --- |
| Subject | `THE OVERSEER PROJECT` / repository `the-overseer` |
| Repository root | `D:\OVERSEER\the-overseer` |
| Assessed revision | `82e15e3c` on branch `ux/first-use` (working tree clean) |
| Assessment date | 2026-09-29 (local `Pacific Standard Time`, UTC-7) |
| Produced by | external assessment session (not an Astra/DeepSeek loop role, not part of the project ledger) |
| Authority | authoritative **only** for what was independently measured here; the project's own `AGENTS.md`, `docs/V1_2_IMPLEMENTATION_SPEC.md` and `docs/IMPLEMENTATION_STATE.md` remain authoritative for **intent** |
| Verification stance | every claim below is tagged `[V]` verified in this pass, `[C]` claimed in repo docs but not re-verified, or `[J]` judgement |

---

## 0. How a consuming agent should use this document

1. Read `AGENTS.md` first for the project's own rules. This document does not override it.
2. Trust only `[V]` rows for facts about the machine and build; treat `[C]` rows as hypotheses to re-check before relying on them.
3. Section 2 gives a five-command re-verification recipe. Run it before making any claim about build/test health — the numbers in this repo's docs were accurate at the assessed revision, but health is environment-dependent here (section 3).
4. Section 13 lists hard constraints. Violating them damages a live user installation and/or the reviewed-branch contract.
5. Do not treat this file as project documentation to maintain. It is an external snapshot; delete it or supersede it rather than appending to it.

---

## 1. Verified state at a glance

| Dimension | State | Tag |
| --- | --- | --- |
| Release build (`Release`) | 0 warnings / 0 errors | `[V]` |
| Test suite | 722 passed, 0 failed, 0 skipped | `[V]` |
| dsh bridge suite (`node --test`) | 30/30 | `[V]` |
| Claude Code bridge suite (`node --test`) | 29/29 | `[V]` |
| Working tree | clean, no untracked non-ignored files | `[V]` |
| Latest candidate | `1.0.0-rc.8`, source `f14dc47`, 568 payload files, unsigned | `[V]` |
| Candidate archive integrity | SHA-256 `CB55897A…61BA93` matches manifest and `.sha256` | `[V]` |
| Third-party runtime dependencies | none (only xunit / test SDK in the test project) | `[V]` |
| Source size | 27,790 lines across 231 files (225 `.cs`, 6 `.xaml`) | `[V]` |
| Test size | 12,727 lines | `[V]` |
| On-disk size | 8,395 MB, of which `artifacts/` is 8,358 MB (142 dirs, 39 ZIPs, 2,122 MB) | `[V]` |
| Tracked size | 4.8 MB | `[V]` |
| Release qualification | **not** reached: 9 of 28 acceptance gates are BLOCKED or UNSUPPORTED | `[V]` |

Headline: the code and its local verification are in good shape; the project is blocked on non-code prerequisites and on one functional gap (Codex approval observability).

---

## 2. Five-command re-verification recipe

Run from the repository root. The workspace SDK is **not** on `PATH` on this machine (section 3.1), so set the environment explicitly:

```powershell
$env:DOTNET_ROOT       = 'D:\OVERSEER\.tools\dotnet'
$env:PATH              = "D:\OVERSEER\.tools\dotnet;$env:PATH"
$env:DOTNET_CLI_HOME   = 'D:\OVERSEER\.tools\cli-home'
$env:NUGET_PACKAGES    = 'D:\OVERSEER\.tools\nuget-packages'
$dotnet                = 'D:\OVERSEER\.tools\dotnet\dotnet.exe'

# 0. always first — prevents the false build failure in section 3.2
& $dotnet build-server shutdown

# 1. build            -> expect "0 warnings, 0 errors"
& $dotnet build TheOverseer.sln -c Release

# 2. tests            -> expect 722 passed, 0 failed, 0 skipped
& $dotnet test TheOverseer.sln -c Release --no-build

# 3. bridges
Push-Location integrations\deepseek-harness; node --test; Pop-Location   # expect 30/30
Push-Location integrations\claude-code;     node --test; Pop-Location   # expect 29/29

# 4. candidate integrity
(Get-FileHash artifacts\rc8\OVERSEER-1.0.0-rc.8-win-x64.zip -Algorithm SHA256).Hash
# expect CB55897A6A75F4B5CBDF975D904DEDCE08347B8BE1F96C7793C726A22461BA93

# 5. hygiene
git status --short          # expect empty
```

Full pre-user qualification runner (long; requires a non-primary display for visible checks):

```powershell
pwsh -NoProfile -File scripts/Test-PreUserRelease.ps1 `
  -CandidateArchivePath artifacts/rc8/OVERSEER-1.0.0-rc.8-win-x64.zip `
  -PreviousArchivePath  artifacts/rc7/OVERSEER-1.0.0-rc.7-win-x64.zip
```

---

## 3. Environment hazards on this machine (read before believing any build failure)

### 3.1 There is no .NET SDK outside the repo

`C:\Program Files\dotnet\dotnet.exe` exists but reports **"No .NET SDKs were found"**. The only SDK is `10.0.401` at `D:\OVERSEER\.tools\dotnet`, which lives **outside** the repository (`../.tools/dotnet`) and is untracked. `global.json` pins `10.0.100` with `rollForward: latestFeature`.

Consequences:

- A fresh clone cannot be built with the machine toolchain as configured.
- Agents must set `DOTNET_ROOT` / `PATH` / `DOTNET_CLI_HOME` / `NUGET_PACKAGES` as in section 2.

### 3.2 Stale build servers cause a misleading failure mode

Symptom, reproducibly seen before being fixed:

```text
error : 无法获取对 "...\Temp\NuGetScratch\lock\<hash>" 的锁定文件访问权限…
生成失败。 0 个警告 3 个错误
```

and, with `--no-restore`:

```text
error MSB3491: 未能向文件 "obj\Release\net10.0\...AssemblyInfoInputs.cache" 写入行。
Access to the path '...\ziv4ie0d.dsx~' is denied.
```

Cause: ~160 leftover `dotnet` / MSBuild / VBCSCompiler processes from an earlier sandboxed run held the NuGet scratch lock and a restricted token. The filesystem was **not** read-only — direct writes to the same directories from PowerShell, and from a `cmd` grandchild process, succeeded throughout.

Fix: `& $dotnet build-server shutdown`, then rebuild. The build then produced 0 warnings / 0 errors immediately.

Not cleaned up by that command: **33 stale `dotnet` processes** were still running after the fix. They are harmless but are the recurrence vector.

### 3.3 The workspace ACLs were partially broken

At session start, every shell command failed at sandbox setup (`grantWrite(D:\OVERSEER)`), and after that writes into `the-overseer\`, `artifacts\` and `src\` were denied. Root cause: the workspace root lacked effective `WRITE_OWNER` for the signed-in account.

Repaired with the DSH sandbox-ACL diagnostic script, which backed up the DACL and verified the granted right. Rollback artifacts:

```text
D:\dsh-acl-recovery\acl-backup-528dd2acfdd34cad881bf785d1251b81.json
D:\dsh-acl-recovery\acl-backup-528dd2acfdd34cad881bf785d1251b81.json.ps1
```

The subtree scan was truncated at 2,000 directories, so deeper directories were never fully audited. If a write is denied at a path for no apparent reason, suspect this class of problem rather than the code.

Note for forensics: this tree contains ACL entries for `PL15\CodexSandboxUsers`, ownership by `PL15\CodexSandboxOffline` on several directories, and orphaned `S-1-5-21-…` ACEs from other accounts. Treat the ACL state as a known-messy area.

### 3.4 Evidence timestamps are UTC, ledger dates are local (UTC-7)

Artifact directory names and JSON stamps use UTC (`run-20260928T065109Z`); `docs/IMPLEMENTATION_STATE.md` entry headers use the local date. They legitimately differ by one day. Do not report this as drift.

---

## 4. Repository topology and the trunk problem

```text
ux/first-use   82e15e3  <- HEAD, 171 commits, tree clean
main           663caf5  <- 146 commits BEHIND; 0 ahead
impl/p1-monitoring   161 behind, 0 ahead
impl/p2-hud          154 behind, 0 ahead
impl/p3-history-voice 146 behind, 0 ahead   (same commit as main)
impl/p4-release      132 behind, 0 ahead   (the V1 release point, 2026-09-22)
agent/ui      fae8aae  62 behind, 4 ahead  (merge-base 3152ec5)
agent/voice   fe2ff0c  62 behind, 5 ahead  (merge-base 3152ec5)
```

`[V]` Findings:

- **Every branch except the two `agent/*` branches is a strict ancestor of `ux/first-use`.** Nothing has diverged on `main` or `impl/*`; the work is simply never merged.
- **`main` is 146 commits stale.** It is not the reviewed trunk it is described as being in `AGENTS.md`.
- **The `impl/p1…p4` branch structure is historical decoration.** All four are ancestors of the active branch.
- **Two orphaned worktrees carry 9 unmerged commits**: `.tools/wt-ui` (`agent/ui`) and `.tools/wt-voice` (`agent/voice`), both clean, both based 62 commits back. Their subject matter (offline-voice baseline, provider choice, expandable project surface with hour strips) already exists at HEAD in evolved form — `VoiceSettings.BaselineDefault`, `NotificationSettingsWindow.xaml`, `ActivityStripBin` in `TaskRowViewModel` — so they are most likely superseded `[J]`. Neither is referenced anywhere in the ledger or handoff.
- Per `AGENTS.md` §7, `main` must stay reviewed and runnable and no agent may merge before an Astra review passes. Do **not** merge `ux/first-use` into `main` as a "cleanup" without that review.

---

## 5. Architecture and invariants to preserve

Four projects, verified by inspection of the solution and sources `[V]`:

| Project | Role |
| --- | --- |
| `src/TheOverseer.Core` | models, monitoring, config, diagnostics, rolling history, voice policy. No WPF/WinForms, no network, no audio. |
| `src/TheOverseer.App` | WPF entry point, composition, platform services (tray, placement, startup registry, shell navigation), HUD lifetime controller, voice I/O adapters, diagnostics runners. |
| `src/TheOverseer.UI` | single `HudWindow`, `HudViewModel` projection, Light/Dark resource dictionaries. |
| `tests/TheOverseer.Tests` | xUnit across readers, discovery, identity, parser, reducer, classifier, attention, health, composition, config, logging, privacy, HUD contract. |

Stack: C# / .NET 10 / WPF / xUnit / System.Text.Json. `[V]` only xunit + test SDK are referenced as packages.

Invariants that must remain true (from `AGENTS.md` §4 and the ledger):

1. Monitoring / state / UI / history / voice separation.
2. Bounded monitoring (per-pass read allowance, tracked-file caps, byte caps).
3. Privacy: metadata only; never prompts, tool arguments, file contents, credentials, titles; logs carry stable codes, counters and numbers only.
4. A monitoring failure is **never** rendered as zero tasks; counts go qualified (`1+`, `?`).
5. Light/Dark share one layout.
6. Voice dedupe and cooldown behaviour.
7. Focus is never stolen: the HUD is created with `ShowActivated=false` plus a bounded startup guard.
8. Windows lifecycle: close hides to tray while a working tray exists, otherwise orderly shutdown.

Composition path (useful entry point for any change):

```text
App.OnStartup
  -> HudComposition.CreateAsync / Prepare
  -> CodexMonitorAdapter + BridgeMonitorAdapter
  -> CompositeMonitorSource
  -> HudApplicationController
```

Harness discovery happens during composition; a newly installed bridge requires an application restart (hot attach is deliberately not implemented).

---

## 6. Process context for a new agent

`AGENTS.md` defines a three-role loop: **Astra** plans -> **DeepSeek** implements -> **Astra** reviews. The state documents are:

| Document | Lines | Role |
| --- | --- | --- |
| `AGENTS.md` | — | operating rules, roles, git contract, scope rule |
| `docs/V1_2_IMPLEMENTATION_SPEC.md` | 1,011 | frozen product source of truth (preserved byte-for-byte from supplied material) |
| `docs/IMPLEMENTATION_STATE.md` | **2,251** | append-only execution ledger (newest entry prepended) |
| `docs/RELEASE_READINESS.md` | 656 | 28-gate acceptance matrix + clean-machine protocol |
| `.agent/HANDOFF.md` | 377 | current plan handoff, gitignored |
| `CHANGELOG.md` | 426 | user-facing changes |
| `README.md` | 256 | current user guide |

Commit distribution `[V]`: `docs` 60, `fix` 42, `feat` 31 (+6 `feat(core)`, +3 `feat(app)`), `test` 19 — out of 171.

`[J]` The ledger has become the project's real memory and directly contradicts `AGENTS.md` §8 ("keep documentation small", "do not turn the repo into an agent-documentation project"). 35% of commits are documentation. A new agent should read the **newest** ledger entry only, not the whole 2,251 lines.

---

## 7. Assessment — scope creep

### 7.1 Procedural discipline: strong `[V]`/`[J]`

- There is a written scope gate (`AGENTS.md` §9): *does this materially improve knowing what agents are doing, or whether an agent needs the user?* Deferred items are recorded rather than silently built.
- The frozen V1 specification was never rewritten; the four-plan unit structure survived.
- **Zero new runtime dependencies** were added across the entire post-V1 phase.
- No speculative framework, no rewrite: Core/App/UI/Tests separation held.

### 7.2 Product scope: real expansion past V1 `[V]`

| Measure | Value |
| --- | --- |
| Total commits | 171 |
| Commits through the V1 release (`impl/p4-release`, 2026-09-22) | ~39 |
| Commits after the declared V1 release | **132 (77%)** |
| …of which `feat` | **30** |
| Header-menu leaf actions | **~38** (7 top-level, up to 3 levels deep) |
| CLI flags / diagnostic modes | 19 / 9 |
| Distinct `config.json` JSON keys | ~45 |
| Evidence directories | 142 |

Defensible post-V1 work (quality, not creep): the 9 audited UI contract defects, the startup focus-theft fix, normal-composition verification, bridge installer validation, upgrade/rollback preservation.

Creep-shaped work: identity/branding, emblem, custom chrome, caption rows, outlined icon, light resize, monitor chart, activity sparkline — presentation polish rounds with no user signal behind them.

### 7.3 The measurable fingerprint of creep `[V]`

`ActivitySparkline`, `ActivitySegments` and `ActivityTooltip` remain in the tree **with no XAML consumer**, retained because their tests exist. This is disclosed in the ledger rather than hidden — to the project's credit — but code kept for its test suite rather than for a user is the clearest creep indicator in the repo.

---

## 8. Assessment — usability

### 8.1 Genuinely good `[V]`

- Read-only by construction; no approvals answered, no prompts sent, no harness data written.
- Focus safety: the HUD previously took the foreground ~0.4 s after launch in **542 of 547** samples, silently swallowing keystrokes. After `WS_EX_NOACTIVATE` + a 20 x 100 ms guard, it measures **0 owned samples of 548** in all four configurations.
- Honest failure surfaces: `?` rather than a fabricated zero, qualified lower bounds, Unknown drawn as a hatch and never as idle, per-source health with own freshness.
- Calibrated first run: brand-new data directory starts muted on the offline Windows speech provider, no network, "Monitoring offline" plus a setup action.
- Keyboard and accessibility basics: `Escape`, Enter/Space activation, 24x24 DIP control minimums, accessible names.

### 8.2 Weak where it matters `[V]`/`[J]`

1. **Surface area is wrong for a glanceable widget.** The window carries 17 chrome controls; the `...` menu carries ~38 leaf actions, duplicated in part by a second tray menu (`src/TheOverseer.App/Desktop/TrayService.cs`) that must be kept consistent by hand.
2. **The main menu is built in C#** — `src/TheOverseer.App/HudApplicationController.cs` around lines 910-1053. Consequences: the menu surface is invisible to XAML review/tooling, and each group's checked state is hand-synchronised inside a `SubmenuOpened` handler. That manual-sync class already produced a real defect (the Recent-hours selector desyncing from the menu).
3. **Usability was validated by audit, not by use.** Nine UI contract defects surfaced in a single review pass, two of which made **user-visible failures render nowhere at all** (a failed voice provider changed nothing on screen; save/tray/navigation errors went to a footer no surface drew). Regression tests now exist; the discovery path shows the UI was built against a contract rather than against a person.
4. **Sample size is one, and that one is the spec author.** The ledger documents live use, so the tool is used daily — by the person who wrote the requirements. That configuration reliably produces menus that grow to 38 items because every addition is individually justified.
5. **The last mile is exactly the untested part.** One display, 96 DPI only; Snap, pointer drag, mixed DPI, sleep/wake, monitor reconnect, audible playback, real dsh/Claude hosts, clean machine and SmartScreen all open.
6. **Concrete friction**: updating means extract to a new folder + copy the data directory + re-point the shortcut + repair the startup entry; hand-editing `config.json` requires closing the app; installing or moving a bridge requires a restart and records an absolute path that breaks when the folder moves.

---

## 9. Assessment — open-source readiness

Not currently shaped as an open-source project. `[V]` gaps, in dependency order:

1. **No LICENSE.** The only `LICENSE` file in the tree is `third_party/codex-monitor-hud/LICENSE` (upstream, MIT). Default copyright therefore applies to the product.
2. **Unresolved rights on supplied assets.** The docs state it themselves: *"do not infer a license from upstream MIT notices or invent rights to supplied assets"* (`docs/RELEASE_READINESS.md:100`). Branding artwork, the preserved `AGENTS.md` and the V1 specification arrived as supplied material.
3. **No CI.** `.github/` is absent; there is no `CONTRIBUTING`, `SECURITY.md`, `CODE_OF_CONDUCT` or issue/PR templates. Verification is nine PowerShell scripts driven by an agent on one desktop.
4. **No clone-and-build path** for a stranger (section 3.1).
5. **Snapshot-shaped documentation.** `README.md` opens with candidate SHAs and "this is an internal candidate, not a published release" — accurate, but not a guide an outside user can act on.
6. **Product adoption hurdles**: Windows-only WPF, unsigned binaries (`NotSigned`, SmartScreen), no installer, no auto-update.

What is already open-source-grade: correct upstream MIT attribution plus `THIRD_PARTY_NOTICES.md`; privacy-by-design that is enforced rather than asserted; a real changelog; a 722-test suite; documentation that discloses its own unverified areas.

---

## 10. Open defects, fragile evidence and things not to trust blindly

| Item | Status | Detail |
| --- | --- | --- |
| Codex approval detection | **open, functional** | Async question detection works and answer-clearing was live-verified. Approvals are not observable through the rollout source; the ledger requires a correlated live event bridge with request/resolution identity, disconnect handling and restart hydration. Never infer approvals from command text or timing, and never answer one. |
| `hud-live-graceful-close` | can never pass | While a working tray exists every close path hides by design; no script can click the tray Exit item. `hud-live-graceful-exit` is the real proof. Documented in the ledger. |
| dsh concurrency test | contention-flaky | `concurrent writers produce whole, non-interleaved lines` failed once while a packaged windowed suite loaded the machine; passes 30/30 in isolation. Re-run before treating a failure as a regression. |
| Release smoke checks | revision-mixed | Several `RELEASE_READINESS.md` gates (R-06, R-16, R-21) cite rc.4/rc.5/rc.6 evidence, explicitly labelled historical. They are not evidence about HEAD. |
| `artifacts/` | 8.4 GB, ignored | 39 ZIPs (2.1 GB) and 142 evidence directories. Never delete: it is the qualification record. Never assume it is regenerable. |
| Live user installation | present | A live HUD, `%LOCALAPPDATA%\OVERSEER`, shortcut and startup entry may exist. Several scripts protect this; manual commands may not. |

Acceptance-gate status `[V]`: of the 28 gates in `docs/RELEASE_READINESS.md`, 9 are BLOCKED or UNSUPPORTED — R-02 (browser download on clean Windows), R-05 (machine with no .NET), R-09 (live Codex approval/input detection), R-11 (live dsh/Claude host qualification), R-14 (physical interaction, DPI scaling), R-19 (sleep/wake, monitor reconnect, mixed DPI), R-22 (audible voice), R-27 (signing, terms, channel), R-28 (clean VM / independent machine).

---

## 11. Documentation drift found

| Location | Issue | Severity |
| --- | --- | --- |
| `README.md:14` | States `1.0.0-rc.7` is "the current packaged internal candidate" while `artifacts/rc8/` exists and the ledger treats rc.8 as current. | minor |
| `README.md:262` | Already references the rc.8 archive path, so the same file disagrees with itself. | minor |
| `RELEASE_READINESS.md` R-15 | Cites 695 tests (rc.6-era) while HEAD measures 722. Labelled as rc.6 evidence, so technically correct but easy to misread. | minor |
| `docs/IMPLEMENTATION_STATE.md` | 2,251 lines, append-only; violates `AGENTS.md` §8. Newest entry is the only current one. | structural |
| `AGENTS.md` §1 | Describes the `impl/p1…p4` branch structure as the execution unit; all four are ancestors of `ux/first-use` and no longer describe how work happens. | structural |

---

## 12. Prioritised backlog with acceptance criteria

Ordered by dependency, not by size. Items 1-2 are decisions only the owner can make.

| # | Item | Why | Acceptance criteria |
| --- | --- | --- | --- |
| 1 | Decide the licence and the redistribution rights for supplied assets (branding, preserved spec/`AGENTS.md`) | Blocks every other open-source step | A `LICENSE` file plus a written rights position for each supplied asset |
| 2 | Decide the Codex approval path: build the correlated event bridge, or withdraw the claim | Only open functional gap; users will report it on day one | Either a qualified live bridge with request/resolution identity, or capability wording that says `unsupported` everywhere it is displayed |
| 3 | Add CI running the existing scripts on a clean runner | The verification apparatus is excellent and currently unshareable | Build + 722 tests + both bridge suites green on a hosted runner from a fresh clone |
| 4 | Make `main` the trunk and retire dead topology | 146-commit-stale trunk; 9 orphaned commits | `ux/first-use` merged into `main` after review; `agent/ui` and `agent/voice` either merged or deleted with their worktrees; `AGENTS.md` §1 updated to match reality |
| 5 | Publish a clone-and-build path | No SDK is discoverable outside the repo | A documented install of .NET 10 plus `dotnet build && dotnet test` succeeding from a fresh clone with no `../.tools` |
| 6 | Unblock the last-mile usability surface | Snap/drag/DPI/audible/live-host are the parts a user feels | Physical interaction, mixed DPI, sleep/wake and audible checks executed and recorded per gate |
| 7 | Cut the `...` menu from ~38 actions to the ~8 used daily; move theme/opacity/light wholly into `Light & voice…` | Glanceability is the product's whole value | Menu leaf count reduced, checks derived from one binding source instead of a hand-synced `SubmenuOpened` handler, UI contract tests still green |
| 8 | Delete `ActivitySparkline`, `ActivitySegments`, `ActivityTooltip` and their tests, or ship them in XAML | Dead code kept alive by its own tests | No XAML-unreachable view-model surface remains, or it is rendered |
| 9 | Replace the 2,251-line append-only ledger with a current-state document plus an archive | `AGENTS.md` §8; agent context cost | Ledger under ~200 lines describing only current state, history preserved elsewhere |
| 10 | Reconcile rc.7/rc.8 documentation and historical counts | Easy to misread; README contradicts itself | `README.md` names one current candidate consistently; historical evidence clearly fenced |

---

## 13. Hard constraints for any agent working here

From `AGENTS.md` §7 plus findings in this assessment:

- **Never** force-push, rewrite `main`, run a broad `git clean -fdx`, reset unrelated work, or delete unrelated branches.
- **Do not merge** `ux/first-use` (or any plan branch) into `main` before an Astra review passes.
- **Do not** replace the user's live installation, `%LOCALAPPDATA%\OVERSEER` data, shortcut or `HKCU\...\Run` entry. Probes must use isolated data directories; scripts that touch the real registry/shortcut are forbidden.
- **Do not** change the user's display scale, open visible windows on the primary display, or run activation checks unless the desktop is idle and the check is explicitly opted into.
- **Do not** record an unexecuted check as PASS. `BLOCKED`, `DEFERRED` and `UNSUPPORTED` are legitimate outcomes; a fabricated pass is not.
- **Do not** mark a missing test as an external-host limitation when it is locally executable — the project has already been reviewed and corrected for exactly this.
- **Do not** add runtime dependencies or frameworks without necessity; V1 scope is frozen.
- **Do not** infer approvals, or answer them; the product is read-only by design.
- Prefer the existing scripts (`scripts/Test-*.ps1`) over ad-hoc commands — they encapsulate the isolation rules above.

---

## 14. Appendix — key paths and raw measurements

Key paths:

```text
src/TheOverseer.App/HudApplicationController.cs       1,397 lines (controller + C#-built menu, ~lines 910-1053)
src/TheOverseer.Core/Monitoring/CodexMonitor.cs       1,191
src/TheOverseer.UI/ViewModels/HudViewModel.cs           966
src/TheOverseer.Core/Monitoring/Bridge/HarnessBridgeSession.cs 881
src/TheOverseer.Core/Monitoring/State/AgentStateReducer.cs     869
src/TheOverseer.UI/HudWindow.xaml                       762
src/TheOverseer.App/Monitoring/CompositeMonitorSource.cs 434
src/TheOverseer.App/Desktop/TrayService.cs             tray menu (overlaps the header menu)
scripts/Test-PreUserRelease.ps1                        six-area runner
scripts/Publish-Release.ps1                            build + manifest + archive hash
scripts/Test-PackagedRelease.ps1                       packaged smoke, display-aware
scripts/Test-ReleaseUpgradePlacement.ps1               mocked three-launch upgrade contract
artifacts/pre-user/run-20260928T065109Z-aab5b5d0/pre-user-report.json
artifacts/rc8/build-manifest.json
docs/RELEASE_READINESS.md                             28-gate matrix
```

Largest test files `[V]`: `HudDispatchTests.cs` 874, `MonitorTests.cs` 571, `CompositeMonitorTests.cs` 522, `HarnessBridgeTests.cs` 479.

Verification commands actually executed in this assessment, with results:

| Command | Result |
| --- | --- |
| `dotnet build-server shutdown` then `dotnet build -c Release` | 0 warnings, 0 errors |
| `dotnet test -c Release --no-build` | 722 passed / 0 failed / 0 skipped |
| `node --test` in `integrations/deepseek-harness` | 30 pass / 0 fail |
| `node --test` in `integrations/claude-code` | 29 pass / 0 fail |
| `Get-FileHash artifacts/rc8/*.zip` | `CB55897A6A75F4B5CBDF975D904DEDCE08347B8BE1F96C7793C726A22461BA93` (matches manifest) |
| `git status --short` | empty |
| Grep `TODO|FIXME|HACK|XXX|NotImplementedException` in `src` | 0 matches |

Removed after use: `artifacts/build-verify.log`, `artifacts/test-verify.log` (temporary logs created by this assessment; both inside the gitignored `artifacts/`).
