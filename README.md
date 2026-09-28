# THE OVERSEER PROJECT

**OVERSEER** is a private, local Windows desktop HUD that answers two questions while
you work: *what are my agents doing?* and *does an agent need me?* One always-visible
window summarises work from **Codex**, **DeepSeek Harness (`dsh`)** and **Claude Code**,
groups it by project, and says plainly when it cannot see something. It is read-only:
OVERSEER never answers an approval, never sends a prompt and never writes into a
harness's data. Runtime data stays in `%LOCALAPPDATA%\OVERSEER`.

- Repository `the-overseer`, solution `TheOverseer.sln`; user-facing changes are recorded in
  [CHANGELOG.md](CHANGELOG.md). Current status, evidence and known limits:
  [docs/IMPLEMENTATION_STATE.md](docs/IMPLEMENTATION_STATE.md); release gates and the acceptance
  matrix: [docs/RELEASE_READINESS.md](docs/RELEASE_READINESS.md).
- The current packaged internal candidate is `1.0.0-rc.7`:
  `artifacts/rc7/OVERSEER-1.0.0-rc.7-win-x64.zip`, SHA-256
  `88531C43F86071E6F7F4581C03FB30818404D874A484B668C50935FF5D93EC74`, built from source
  `5aba6c2` with a clean tree, 568 payload files including 75 integration assets, unsigned.
  The question-answer clearing fix in current source is newer than this archive.
  It is an **internal candidate**, not a published release. Public release is **not** claimed:
  clean-machine/browser-download, live-harness, signing/terms/distribution and physical
  display/audio gates are still open, and
  [docs/RELEASE_READINESS.md](docs/RELEASE_READINESS.md) records each one with its exact
  missing prerequisite.

## Install and run

Unzip `OVERSEER-<version>-win-x64.zip` into a per-user folder (no elevation needed), keep the
extracted folder together, and run `TheOverseer.App.exe`. It is a self-contained `win-x64` folder
build: it loads the .NET runtime from its own directory and needs no system .NET, no SDK and no
repository checkout. Runtime data lives under `%LOCALAPPDATA%\OVERSEER` (`config.json`,
`state.json`, `voicepacks/`, `voiceclips/`, `bridges/`, `spool/`, `logs/`); pass
`--data-directory PATH` to run fully isolated.

The archive contains the complete publish output plus `README.md`, `CHANGELOG.md`,
`THIRD_PARTY_NOTICES.md`, `release.json`, `licenses/`, `examples/voicepacks/example/` and
`integrations/` (both optional bridges with notices, installers and tests). No repository files,
caches, logs or private data are included.

**Supported platform.** Self-contained `win-x64` ZIP for a standard (non-elevated) Windows user,
no .NET SDK required. The runs recorded in this repository were made on one development machine
at 100% display scale, and **no OS build number is recorded in that evidence**, so no specific
Windows edition or version is claimed. ARM64, WOW64/x86, WSL-hosted, macOS and Linux are neither
supported nor tested.

## Controls

Every control below is inside a window that is shown with `ShowActivated=false`, so status updates,
theme changes and notifications never take your focus.

| Control | What it does |
| --- | --- |
| Summary half `N NEED YOU` / `N WORKING` | Switches to the matching **Needs you** or **Working** filter and expands the list. |
| Health control (dot + label) | Opens per-source detail: each source's own state, freshness, capabilities and one next action. |
| `LIGHT ON` / `LIGHT OFF` | Turns the status light on or off; its colour matches the current status. |
| Mute button (`M` muted / `V` unmuted) | Mutes or unmutes voice immediately. |
| Expand / collapse button, or `Escape` | Expands or collapses the task list (`Escape` collapses it in the compact view). |
| Maximize button (`□` / `❐`) | Maximizes or restores; hover it for Windows Snap layouts (or use Win+Z / Win+Arrow). |
| Menu button (`⋯`) | Opens the header menu below; available even without a tray. |
| Close (`✕`, Alt+F4, system menu) | Hides to the tray while a working tray can restore it (the tooltip says so); without a tray it runs the same orderly shutdown. |
| Any drag strip (header, expanded meta line) | Moves the HUD and saves the position; buttons and the summary keep their clicks. |
| Status light, or the outer 6 DIP border | Drag the light to resize it (6–128 DIP), the border to resize the window. |
| Project header row, or a `History` toggle | Expands or collapses that project's tasks, or discloses that project's or task's hour strip without activating it; observation, history and voice keep running while collapsed. |
| Task row, or right-click → `Copy task id` | Activates a resolved Desktop task through its validated `codex://threads/<id>` route (other harnesses offer the id for copying instead), or copies just the id. A focused row activates with Enter or Space. |

The header menu (`⋯`) contains **Always on top** (checkable), **Theme** (Dark / Light), **View**
▸ *Recent interval* (1 / 2 / 4 / 8 / 24 hours), *Reset position*, *Opacity* (75 / 85 / 96 /
100%), *Status light* (Right / Left / Top / Bottom / Off, then *Width* 24 / 32 / 48 / 64 / 96 /
128), *Expand all projects*, *Collapse all projects*, *Compact / expanded HUD*, *Maximize /
restore*, *Minimize*, *Start with Windows* and *Monitoring details*; then **Light & voice…**,
**Projects & sources…** (lists discovered bridge sources and can disable one, applied at the next
start), **Hide to tray** (enabled only while a tray can restore the window) and **Exit**. The tray
icon offers Show/Hide HUD (double-click toggles it), Light/Dark mode, Always on top, Opacity presets,
Mute voice, Start with Windows, Reset position, a monitoring status line, Light & voice… and Exit.

### Views, counts and history

**Now** (default) shows current work and attention plus completions and errors from the last ten
minutes; **Recent** shows tasks with observed activity in the last 1, 2, 4, 8 or 24 hours, chosen
from the **Recent hours** selector next to the view tabs (visible only while Recent is selected);
**This session** shows tasks touched since OVERSEER started. The selected view and window are saved.
Below the tabs, the **All / Needs you / Working** chips filter which rows are listed, with All
always present as the explicit reset. Headline counts always describe *all* monitored work,
independently of the selected view, and the Needs-you filter spans the whole snapshot so an old
pending request is never hidden behind a time window.

Project rows carry harness badges, working/needs-you counts and a last-hour strip; task rows carry
harness, state, action, turn elapsed time and their own strip. The shared hour axis and legend
appear whenever any project *or* task history is disclosed, and Unknown is drawn as a hatch because
it means "nothing was observed", never idle.

### First run

A brand-new data directory starts **muted**, with the offline Windows speech provider as the
baseline: the nine fixed catalog phrases are synthesised locally through the installed English
voice, nothing is downloaded and no task text leaves the machine. With no harness installed the HUD
shows honest unknown health (`Monitoring offline`, `?` counts) and a setup action in
**Projects & sources…** — never a fabricated zero. An existing `config.json` is **never reset**:
provider, mute, pack, endpoint, per-event switches, assigned recordings, roots, mappings and window
state are preserved.

## Optional harness integrations (experimental)

**Codex attention limits.** Async questions can be detected from tool-call metadata.
Current source also matches the desktop's structured answer envelope to individual
questions, clearing attention once that request's questions are answered. Unknown or
oversized envelopes retain the existing turn-boundary fallback. Approval dialogs
are not detected by the current Codex rollout source; an ordinary command/tool call
is not treated as proof of a pending approval. See Monitoring details for capability
status. No question or answer text is retained.

[integrations/README.md](integrations/README.md) is the index. Both bridges are opt-in,
metadata-only and reversible, and both are **experimental**: each is contract-tested, and
neither has been exercised against a live host here, so no host version is claimed as supported.

| Directory | Harness | Needs | Install |
| --- | --- | --- | --- |
| `integrations/deepseek-harness/` | DeepSeek Harness (`dsh`) | Node.js 20+ in the host | `dsh plugin --profile <name> add "<absolute path>"` |
| `integrations/claude-code/` | Claude Code | Node.js 18+ | `node install.mjs "<settings.json>" --apply` (without `--apply` it only previews) |

Three things matter before you install one:

- **Restart OVERSEER after installing, changing or removing a bridge.** Discovery runs when
  the monitor is composed at startup; hot attach is not implemented.
- **Keep the extracted integration directory somewhere permanent.** Neither host copies the
  bridge: `dsh` links the directory (a junction on Windows), and the Claude installer records
  an absolute path to `bridge.mjs` in your settings file. Read
  [integrations/deepseek-harness/README.md](integrations/deepseek-harness/README.md) first —
  the `dsh` side has no preview flag.
- **Rebinding is the recovery path once that path moves**:
  `node install.mjs "<settings.json>" --bridge="<new path>\bridge.mjs" --apply`. `--apply`
  refuses to write unless the hook command it is about to record has actually run against a
  throwaway spool, so a settings file can never point at a missing or non-runnable bridge.

Both spools live inside OVERSEER's data directory (`%LOCALAPPDATA%\OVERSEER\bridges\…` and
`…\spool\claude\…`), so installing a later OVERSEER version cannot move or orphan a bridge's data.
Only metadata is written — session identity, turn and tool boundaries, request ids, plan changes and
outcomes; prompts, tool arguments, file contents and credentials are never read or stored.

## Update, rollback and what is tested

Runtime data is separate from the application folder, so updating means extracting the new version
into its own new folder and starting that copy — never extract over the running installation.
**Take a copy of `%LOCALAPPDATA%\OVERSEER` before updating**, then re-point your shortcut and repair
the startup entry once; keep the old folder until the new version is accepted. Both rollback routes
are written up in
[docs/UPGRADE_AND_ROLLBACK.md](docs/UPGRADE_AND_ROLLBACK.md): the tested *older build on newer data*
route, and the preferred *pre-update backup/restore* route. `scripts/Test-ReleaseUpgrade.ps1`
exercises both on copies only: it seeds one isolated data directory with the complete previous-release
state (every monitor root and limit, logging field, HUD setting, window mode, voice setting, project
mapping, alias, disabled source and saved placement, plus a custom recording and a populated bridge
spool); runs the previous build, copies the data directory, runs the candidate on the copy and
compares every JSON leaf of `config.json`, `state.json` and the spool documents by exact JSON path
(89 paths) plus recordings by SHA-256; runs the **previous build again against a copy of the data the
candidate wrote**; and verifies the backup route by damaging the candidate-written copy
(`config.json` and `bridges/` removed), restoring the pre-update backup over it and comparing the
result with the pre-update data. With no non-primary display and no `-AllowVisibleChecksOnPrimary` it
launches nothing, writes deferred evidence and exits `1` — read the evidence, not the exit code
alone.

The same script checks update targets: the production `StartupRegistration` helper is driven
through a probe against a file-backed fake registry store (never the real `HKCU\…\Run` value) to
confirm the recorded command quotes the candidate executable, carries `--start-in-tray`, rejects
`dotnet.exe`, reports a stale recorded path after a package move and preserves the opt-in, and a
copied `.lnk` inside the evidence directory must follow the candidate directory; your real shortcut
and startup entry are never read or written.
`scripts/Test-ReleaseUpgradePlacement.ps1` runs the same comparison with display discovery and
launches mocked in three cases (no secondary display → deferred and failing qualification;
secondary display → three launches; explicit `-AllowVisibleChecksOnPrimary` opt-in); the recorded
run in `artifacts/upgrade-placement-tests/33c099b52c714915b2c4282cec15b0aa/` passes 25/25 checks in
both windowed cases.

A bridge installed from the old folder keeps the absolute path it recorded, and an archive
extraction cannot rewrite a hook setting — re-run the shipped installer (or rebind) after the move.
Not covered, and not claimed: an in-place upgrade over an installed folder, a data directory moved to
a different absolute path, live bridge rebinding on a real host (a stable spool proves the data
survived, not that a real hook still fires), physical sleep/wake, monitor removal/reconnection, mixed
DPI, audible playback by ear, and a machine with no .NET runtime.

## Build and test from source

On Windows x64 with the .NET 10 SDK on `PATH`, from this repository root:
`dotnet restore; dotnet build; dotnet test; dotnet run --project src/TheOverseer.App`. The workspace
SDK lives outside Git in `../.tools/dotnet`; if it is not on `PATH`, set `DOTNET_ROOT`, prepend it to
`PATH`, and point `DOTNET_CLI_HOME` and `NUGET_PACKAGES` at `../.tools/cli-home` and
`../.tools/nuget-packages`. `scripts/Publish-Release.ps1` publishes `win-x64` self-contained and
records `build-manifest.json` (source commit, SDK version, payload list, archive SHA-256; it refuses
a dirty tree unless `-AllowDirtyTree`), while `scripts/Test-PackagedRelease.ps1` extracts a ZIP into a
fresh directory and runs the packaged checks with a sanitized child environment (a headless run
records every windowed check as skipped with a reason and exits `1` instead of dropping it).

## Diagnostics modes

Finite modes that use an isolated data directory when you pass one and never replace production
input: `--monitor-debug [--duration N] [--redact-labels]` runs the console monitor (capabilities,
health with reason codes, real counts, one row per task; exit `0` readable, `2` essential monitoring
unavailable, `1` bad arguments); `--hud-fixture --output-directory DIR` renders the synthetic preview
and window-size matrix; `--hud-smoke [--duration N] [--no-tray]` composes production against a
synthetic monitor and exits cleanly; `--hud-integration-check` drives invented Codex and bridge
producers through the real readers, composite and controller, and `--hud-composition-check` drives
the real startup composition (config load, discovery, provider selection, controller), isolating
itself under the evidence root and refusing an explicit request for your live data directory;
`--hud-startup-check` reversibly checks OVERSEER's own Run entry; `--voice-smoke [--audible]` proves
the pack, policy and TTS contracts with synthetic tones and an owned loopback endpoint, and
`--voice-pack-check` loads and validates the pack in your data directory without playing anything.
`--placement-monitor DEVICE` starts the HUD on a specific monitor for diagnostics, and
`scripts/Test-WindowScaling.ps1` exercises native resize, maximize/restore, bound persistence and the
Snap hover target on a display you name, deferring instead of moving windows onto the display you are
using.

## Configuration and logs

`%LOCALAPPDATA%\OVERSEER\config.json` is optional; every limit is clamped, and a malformed file
is preserved as `config.json.corrupt-<timestamp>` before defaults are used. Keys include
`codexHome`, `additionalRoots`, `limits`, `logging`, `disabledHarnessSources`,
`maximumHarnessSources`, `harnessRoots` and `hud`:

```json
{
  "limits": { "maxTrackedFiles": 64, "globalBytesPerPass": 4194304, "staleLeaseMinutes": 15 },
  "logging": { "enabled": true, "maxBytes": 1048576, "level": "info" },
  "hud": { "theme": "dark", "alwaysOnTop": true, "opacity": 0.96, "taskView": "now", "recentHours": 2 }
}
```

HUD defaults: dark theme, always on top, 96% opacity, Now view, 2-hour Recent window, a right-side
light 32 DIP wide. Opacity is clamped to 0.65–1.0, the light to 6–128 DIP and Recent hours to
1–24. `state.json` stores placement as display-independent pixels relative to the saved monitor's
working area plus the DPI scale, so a DPI change keeps the same physical spot and a removed
monitor is clamped back onto a reachable one. Editing `config.json` by hand requires closing
OVERSEER first. Logs go to `logs\overseer.log` with one rolling backup and contain only stable
codes, counters and numbers — never titles, paths, prompts, assistant text, questions, answers,
command lines or credentials.

## Project layout

`TheOverseer.App` is the WPF entry point and owns platform services (tray, placement, startup
registry, shell navigation), the HUD lifetime controller, the voice I/O adapters and the diagnostics
modes. `TheOverseer.Core` holds models, monitoring, configuration, diagnostics, rolling history and
voice policy, with no WPF/WinForms and no network or audio code. `TheOverseer.UI` contains the single
`HudWindow`, the `HudViewModel` projection and the Light/Dark resource dictionaries;
`TheOverseer.Tests` is the xUnit suite across readers, discovery, identity, parser, reducer,
classifier, attention, health, composition, configuration, logging, privacy and the HUD contract. The
stack is C# / .NET 10 LTS / WPF / xUnit / System.Text.Json with no additional runtime dependency.

`AGENTS.md` and `docs/V1_2_IMPLEMENTATION_SPEC.md` are preserved byte-for-byte from the supplied
documents; their older product naming is superseded and the V1 scope is unchanged. Monitoring
plumbing is adapted from `https://github.com/LH-03/codex-monitor-hud` at revision
`df1d5b9e3d4b701de979b5c84a006db7cca5112f` (MIT) — see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and `third_party/codex-monitor-hud/LICENSE`; the
artwork is preserved in `design/branding/overseer-source.png`. Monitoring health is separate from
task state, so a broken monitor is never shown as zero tasks, stale tasks are excluded from
authoritative counts (which are reported as partial, `1+` / `?`), and every source carries its own
health, freshness and coverage within one shared per-pass read allowance.

## Automated pre-user testing

With .NET 10, Node and PowerShell 7 available, run the six-area checks against two
trusted local packages (replace these archive paths for a newer candidate):

```powershell
pwsh -NoProfile -File scripts/Test-PreUserRelease.ps1 `
  -CandidateArchivePath artifacts/rc8/OVERSEER-1.0.0-rc.8-win-x64.zip `
  -PreviousArchivePath artifacts/rc7/OVERSEER-1.0.0-rc.7-win-x64.zip
```

The runner builds/tests source, validates report/comparison controls, tests the extracted package and both bridges, runs
upgrade/rollback in HUD and Window modes, and exercises native scaling. It creates
a fresh `artifacts/pre-user/run-*/pre-user-report.json` with logs and six-area results.
It retains the isolated extraction for investigation. A non-primary display is required
for visible checks; no personal settings, startup entries or harness profiles are changed.
The two second-launch scenarios deliberately activate the HUD, so they are deferred
by default. Add `-IncludeActivationChecks` when the desktop is idle to exercise them.
Other lifecycle checks still run. Source and archive commit/hash identities are recorded
separately; testing current source does not certify an older binary.

An automated pass is **not** full release qualification. The report retains clean-machine
download/SmartScreen, live harness setup, Codex approval observability, physical
display/sleep/Snap, Windows logon/tray interaction and audible voice checks as remaining
work. Synthetic events and silent audio checks are labeled accordingly.

## Historical release evidence

Everything below belongs to an earlier revision and is **not** a claim about the current working
tree; each item names the source commit recorded in its evidence.

- `1.0.0-rc.4` (source `c18822d`): 41 packaged checks passed, including lifecycle, voice and
  legacy-settings upgrade checks — `artifacts/rc4/smoke/run-20260927T232228Z-5d48094d/`.
- `1.0.0-rc.5` (source `389cb15`): 14/14 production-path integration checks and 9/9
  `rc.4 → rc.5` upgrade checks under `artifacts/rc5/`, the latter with the earlier comparison
  script, which recorded rollback, shortcut and startup-entry checks as not performed.
- Earlier candidates (`artifacts/rc1`…`artifacts/rc3`, `artifacts/review4-final`) are superseded.

Current-tree evidence is the normal-composition check (26/26,
`artifacts/composition-check-noarg/`), the mocked upgrade/rollback placement run (25/25 per windowed
case, `artifacts/upgrade-placement-tests/33c099b52c714915b2c4282cec15b0aa/`), the isolated window
scaling run (`artifacts/scaling-dev2-184414/window-scaling-evidence.json`) and the 46 HUD renders in
`artifacts/ui-cleanup-renders/`. Outstanding gates are tracked in
[docs/IMPLEMENTATION_STATE.md](docs/IMPLEMENTATION_STATE.md),
[docs/RELEASE_READINESS.md](docs/RELEASE_READINESS.md),
[docs/UPGRADE_AND_ROLLBACK.md](docs/UPGRADE_AND_ROLLBACK.md) and
[docs/WINDOW_SCALING_PLAN.md](docs/WINDOW_SCALING_PLAN.md), with deferred visual work in
[docs/UI_SPEC_v0.2.md](docs/UI_SPEC_v0.2.md).
