# Tester handoff

Goal: on a Windows x64 machine, build OVERSEER, connect one local source, and report
whether the HUD accurately reflects a small work session. Allow about 20–30 minutes
after installing prerequisites. Use a harmless task in a project you can show safely.

Start with [README → Clone and run](../README.md#clone-and-run). All source commands
below run in the **repository root in PowerShell 7**. A packaged tester instead uses
the [application ZIP path](#using-an-application-zip); Git and the SDK are not required
for that path. Use only an explicitly supplied candidate or a published release
whose version/hash you can record. A local dirty candidate is not a published release.

## 1. Record what you are testing

For a source checkout:

```powershell
git rev-parse HEAD
git status --short
dotnet --version
```

Include the SHA and whether the tree has local changes in your report. A branch name
alone does not identify the tested code. Use Windows Settings → System → About for
the Windows build, and Display for scaling. Record the agent host and engine versions
from that host; do not infer them from a separate CLI installation.

For historical comparison, the **RC21 ZIP** was built from `c227dd9`. The task-launch
exception fix `3767d4a` landed later in [PR #1](https://github.com/WinChurchill/the-overseer/pull/1).
Record the source commit or archive hash of the build you actually test.

## 2. First launch and source setup

Use the README's separate `$testData` directory and keep its path for the whole test.
It isolates OVERSEER settings/history/logs, but the app still observes your local
sources. It does not isolate or reconfigure Codex or bridge producers.

- With no usable source, check that the coverage control explains unknown/offline
  state and offers a setup action. A blank task list alone is not a successful connection.
- With local Codex, start a short task, then inspect **Now**. If nothing appears,
  verify the actual `CODEX_HOME` and health details; use `--codex-home` for an override.
- For DSH or Claude, use the [source table](../README.md#connect-a-source) and the
  linked installer guide. Keep bridge code at a permanent path, preview changes,
  retain backups, and restart both the host and OVERSEER after installation.
  Do not install every bridge just to complete this checklist.
- Voice starts muted with the included Kokoro pack in a new data folder. Existing
  saved settings are preserved; select **Use included Kokoro voice** if migrating
  from a provider that fails. Windows speech is optional and unqualified for this alpha.
  Inspect **⋯ → Light & voice…** and **Projects & sources…** before changing options.

### No host available yet?

After building, this finite diagnostic renders the real HUD with invented tasks,
then exits. It reads no live sessions and does not install bridges or register startup:

```powershell
dotnet run --project src/TheOverseer.App/TheOverseer.App.csproj -c Release --no-build --no-restore -- --hud-fixture --output-directory artifacts/tester-hud
Invoke-Item artifacts/tester-hud/dark-readme-window.png
```

This checks that WPF can render, **not** live monitoring. Mark source checks as
`NOT RUN` and explain which host/access is missing. README theme images were copied
unchanged from `dark-readme-window.png` and `light-readme-window.png`: the larger
720 × 640 DIP window using `SyntheticHudScenarios.ReadmeOverview`. The compact
image is an earlier synthetic fixture.

## 3. Run one short session

Record each row as `PASS`, `FAIL`, or `NOT RUN` with a short observation. Do not turn
an unsupported capability into a pass. Answer and approve only inside the agent host.

| Check | Expected result |
| --- | --- |
| First launch | HUD opens; new settings start muted; coverage is explained even without a source. |
| Work and finish | An ordinary task appears under the expected project, including a user-facing Codex chat created by another agent or a handoff. Observed work and explicit completion/error update its state. Record missing or stuck transitions. |
| Needs input | If the host produces a supported question event, the task enters Needs you. A correlated reply clears its request. Codex/Claude turn boundaries clear remaining turn-scoped requests; DSH 1.1.0 questions remain pending across turns until a correlated answer or authoritative state clears them. |
| Approval | **Codex: Unsupported.** On DSH/Claude, only test a harmless action you would normally approve; record wait/resume or mark NOT RUN. Never force an unsafe action to trigger this check. |
| Views and history | Try Now, Recent, This session, Needs you, Working, project expansion and History. Header totals still summarize all observed work. Striped/unknown history is unobserved time. |
| Settings | Change theme and one harmless display option; changes save automatically. Exit and relaunch with the same data folder. The saved choices return. |
| Close and restore | Close hides to a usable tray; restoring from the tray or launching again with the same data folder returns the HUD. **⋯ → Exit** stops it. Record tray behavior separately from launch-to-restore. |
| Focus | While typing in another app, ordinary HUD updates should not take focus. Intentional restore/navigation may activate a window. |
| Task navigation | For a resolved Codex desktop task, record whether the exact chat opens. An accepted request alone is not proof. Other sources can offer task-ID copying. |
| Optional voice | Select **Use included Kokoro voice**, Preview, and listen. Then unmute for a supported event if wanted. Record audible output separately from successful playback status; remute afterward. Windows speech is an optional diagnostic, not the alpha baseline. |
| Focus pins | Open **Focus…**, add a reminder and restart. Preview imported JSON before confirming; malformed JSON changes no pins. Removing a pin must leave live task counts unchanged. |

Closing a Codex async question card does not produce a cancellation record that
OVERSEER can observe. The row says **Question observed**; after the existing
15-minute lease it becomes **Input status unconfirmed**, excluded from confirmed
Needs you counts. Its identity stays available for a later correlated answer.
Fresh work can remain current independently. Do not interpret this as immediate
dismissal detection.

The opt-in Claude Projects observer reads loaded desktop Threads/Overview views
without activating them. A Projects directory is not status evidence. Check visible
and background behavior separately; record missing/unmounted views as limited
coverage, not zero work. Account-wide cloud coverage remains unqualified.
Worker rows show the dedicated thread title when exposed, with a short-ID fallback.
Renaming a thread must leave its identity, state, timers and notification quiet;
message snippets and aggregate card labels are excluded.

Stop at a reproducible failure, retain the result, and continue unrelated checks if
safe. You do not need to enable Start with Windows, change displays, configure a
network speech service or complete an hour-long soak for this first session.

## 4. Report and hand back

Send a sanitized result through the channel that supplied the build or use the
[repository bug form](https://github.com/WinChurchill/the-overseer/issues/new?template=bug_report.yml).
Copy this template:

```text
Source SHA + local changes, OR package version + ZIP SHA-256:
Windows version/build, CPU architecture, display scale:
.NET SDK (source builds only):
Agent host + engine version and local/WSL/SSH/cloud environment:
Source tested and relevant coverage reason:
Data: new tester folder / reused settings (redact personal path):
Checklist: PASS / FAIL / NOT RUN for each row, with observations:
Failure: steps → expected → observed:
Frequency and whether a new tester data folder reproduces it:
Sanitized screenshot/error excerpt (optional):
Anything difficult to find or understand in setup/UI/docs:
```

Do not send raw rollouts, transcripts, credentials, full settings/data directories
or unreviewed logs. Screenshots may contain task titles, project paths and IDs;
redact them first. Use the [security route](../SECURITY.md) for sensitive findings.

Exit the app when done. Keep test data until the result is understood. Remove any
test bridge with its documented uninstaller before moving its files; preserve
unrelated host settings. See [upgrade and rollback](UPGRADE_AND_ROLLBACK.md) for
backup, update and uninstall procedures.

## Troubleshooting

| Symptom | Next step |
| --- | --- |
| Git reports repository not found or access denied | Confirm the GitHub account has private repository access. This is not an app failure; ask Winston for access. |
| `dotnet` missing or no compatible SDK | Install x64 .NET 10 **SDK**, reopen PowerShell and run `dotnet --list-sdks`. A runtime-only install cannot build. |
| Restore fails | Retain the NuGet/network/certificate error. Check access to NuGet and retry restore before building; do not disable certificate checks. |
| Build succeeded but launch fails | Run from the repo root with the README's project/configuration arguments. Record the full error and source SHA. |
| WPF reports `0x800700CE` | The extraction or checkout path may be too long. Use a shorter folder you own, retaining the failed result. |
| No tasks / limited coverage | Open coverage details; check source home, host execution environment and fresh activity. A bridge needs the documented spool binding and an OVERSEER restart. |
| Old settings or another window appears | Use the same intended `$testData`; a second launch for that data directory restores the existing instance. Exit that instance before changing launch options. |
| Preview is silent | Select **Use included Kokoro voice**, check output device/volume and Preview again. Check assigned WAV overrides separately. Windows speech remains optional and unqualified. |
| Claude desktop status or Windows speech fails only from an agent workspace | Extract the application ZIP into the normal per-user folder below. A sandbox workspace can give native EXEs Low integrity, restricting desktop accessibility and speech. Record the launch path and coverage reason. |

## Using an application ZIP

Ask Winston for a versioned **application** ZIP plus its expected SHA-256. GitHub's
source-code ZIP cannot be launched as an installed app. Packaged builds include the
.NET runtime; Git, the SDK, Node and a checkout are unnecessary for the HUD itself.
Optional bridge producers still need their documented host-side Node runtime.

In PowerShell, replace the paths below with the supplied archive and a **new**, short
destination you own:

```powershell
$archive = 'C:\path\to\OVERSEER-<version>-win-x64.zip'
Get-FileHash -LiteralPath $archive -Algorithm SHA256
```

Compare the result with the supplied hash **before** extracting. If it differs, stop
and report it. See [download verification](RELEASE_VERIFICATION.md) for an automatic
checksum comparison and optional GitHub build-provenance verification. When it matches:

```powershell
$appFolder = Join-Path $env:LOCALAPPDATA 'OVERSEER-App\test-build'
if (Test-Path -LiteralPath $appFolder) { throw 'Choose a new extraction folder.' }
Expand-Archive -LiteralPath $archive -DestinationPath $appFolder
Set-Location $appFolder
Get-Content release.json
$testData = Join-Path $env:LOCALAPPDATA 'OVERSEER-Tester\package'
.\TheOverseer.App.exe --data-directory "$testData"
```

Keep the whole extracted folder together. Include version, source commit and
`sourceTreeDirty` from `release.json`, plus the archive hash, in your report.
Keep this application folder outside an agent sandbox/worktree. In the local
qualification, the same native binaries saw Claude's loaded background workers
and synthesized Windows speech from this kind of normal user folder; they could
not do so after inheriting the workspace's Low integrity label.
This alpha candidate is unsigned: keep Windows protections enabled and record any
warning or policy block. Continue with checklist sections 2–4 above.

## What this handoff does not certify

### Publication alpha manual checks

Before publishing `1.0.0-alpha.1`, record PASS/FAIL/NOT RUN against the actual candidate
version and ZIP hash. A local dirty build does not identify a clean published commit.
Local alpha `.20261102.3` passes 930 source tests and 46 exact-package checks with no
deferrals. Secondary-screen summary/divider dragging and theme persistence were also
checked in the actual app. These results do not substitute for the host/other-machine
checks below; see release readiness for the exact ZIP identity and retained failures.

1. Compare exposed Claude worker titles/statuses with the HUD, then rename one
   harmless test thread in Claude. The existing row updates without a new task,
   timer reset or voice cue. Leave an Overview loaded in the background; close or
   unmount it and check that coverage becomes uncertain instead of implying no work.
2. Exercise a supported work → input → reply flow and listen for one Kokoro cue.
   Test actual Claude/DSH transitions separately; Codex approvals remain Unsupported.
3. With the desktop idle, check summary dragging, header buttons, task navigation,
   hide/restore, Exit and saved settings. Native mouse-capture assertions and automated
   activation/restore now pass in the local alpha. Physical tray clicks and actual
   task navigation remain separate checks; they were not performed on the primary screen.
   Drag the light/interface edge both ways: the shared edge should follow the pointer,
   stop at its size limits and respond immediately when reversing direction.
4. Try the exact ZIP on a clean Windows x64 machine, record unsigned-app warnings,
   and complete relevant display/power/device checks from release readiness.
5. Review/commit/push the intended source, obtain and qualify the exact CI alpha
   asset, then follow the source/prerelease/website publication steps in
   [release readiness](RELEASE_READINESS.md#concrete-owner-handoff).

For the November 2 local candidate, use the included Kokoro pack for voice testing.
Its actual Codex-question-to-native-playback path passed, including the owner's
confirmation of hearing one cue. This does not qualify other hosts or devices.
Earlier native/VSTest Windows speech checks failed with `80045040` from the Low
integrity workspace. The exact packaged native app now passes silent synthesis
and preview from a normal user folder. Windows speech acoustic output remains
unverified; Kokoro stays the alpha baseline. Earlier failures remain in the evidence.

Automated fixtures and prior CI establish only their recorded source/package checks.
Clean Windows download/SmartScreen and no-runtime installation, complete live DSH and
Claude behavior, physical mixed-DPI/Snap, sleep/wake, monitor reconnect, audible
devices, logon/startup and an actual-host soak still need version-bound evidence.
Other operating systems and ARM64/x86 are unqualified. Full gates and historical
results remain in [release readiness](RELEASE_READINESS.md) and
[implementation state](IMPLEMENTATION_STATE.md); this first-session checklist does
not approve publication.
