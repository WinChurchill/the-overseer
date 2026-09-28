# Upgrade and rollback

Every claim here is produced by `scripts/Test-ReleaseUpgrade.ps1`; what it does not test is
listed under "Not verified" rather than implied.

## Supported upgrade path

1. Stop OVERSEER.
2. Back up the data directory: copy `%LOCALAPPDATA%\OVERSEER` (or the directory passed to
   `--data-directory`) to a sibling folder. Keep it until the new version has been used.
3. Extract the new archive into its **own new folder**. Never extract over the running
   installation: a packaged version is a self-contained folder, and the data directory
   lives outside it, so installing, moving or removing a version does not move or orphan
   the data directory or the bridge spools inside it.
4. Start the new build. It reads the same data directory (`config.json`, `state.json`,
   `voicepacks/`, `bridges/`).
5. If you installed a bridge integration, re-run its shipped installer (see "Integration
   records" below).
6. Re-point your shortcut at the new folder's `TheOverseer.App.exe` and repair the startup
   entry once (Settings -> "Start with Windows"). Delete the backup when done.

## Running the upgrade check

```powershell
pwsh -NoProfile -File scripts/Test-ReleaseUpgrade.ps1 `
    -PreviousArchivePath artifacts/rc5/OVERSEER-1.0.0-rc.5-win-x64.zip `
    -CandidateArchivePath artifacts/rc6/OVERSEER-1.0.0-rc.6-win-x64.zip `
    -EvidenceDirectory artifacts/rc6/upgrade
```

Both archives are extracted into the evidence directory, one isolated data directory is
seeded with a previous release's complete settings, the previous build runs against it, a
copy is taken, the candidate runs against that copy, and the previous build runs once more
against a copy of what the candidate wrote (the rollback leg). Every run uses
`--hud-smoke` and opens a window on a non-primary display; on a single-display machine the
windowed comparison is deferred unless `-AllowVisibleChecksOnPrimary` is passed
(`-KeepExtraction` keeps the extracted packages).

`config.json` and `state.json` are flattened to one leaf per scalar and compared by exact
JSON path (89 paths with the current seed). A difference fails the run unless it is an
*addition* or is covered by a recorded `normalizationJustification`. Nothing is silently
ignored: the evidence lists every difference with its path, before/after values, kind and
justification. Property-name casing is not a difference (both stores read names
case-insensitively); array order and length are.

## What is verified as preserved

- `config.json`: `codexHome`; `additionalRoots` (3 entries); all six `limits` fields;
  `logging` enabled/maxBytes/level; `runtimeActivitySource`; every HUD setting (theme,
  alwaysOnTop, opacity, startWithWindows, taskView, recentHours, lightPosition,
  lightSize); voice muted/provider/packId/endpoint/timeoutSeconds, all nine
  `voice.events` overrides and every `voice.clips` assignment; `project` mappings (3),
  aliases (3), maxAncestorChecks, maxCacheEntries; `disabledHarnessSources` (3);
  `harnessRoots` (3); `maximumHarnessSources`.
- `state.json`: `version`, `isExpanded`, `monitorDevice` and the window view/pinned mode
  are compared strictly. Saved offsets, `savedAtUtc` and window geometry are re-derived by
  whichever HUD runs, so those are reported as *explained* with a justification, not
  ignored.
- Every file under `voicepacks/` is byte-identical (sha256), so a custom recording and the
  clip files it references survive.
- The bridge spool keeps its content, not just its files: each seeded `events.jsonl`
  record, the `current.json` current-state document and `health.json` are compared field
  by field.
- Every non-log file the previous build left in the data directory is still present.
- The startup target is built by the production helper
  (`TheOverseer.App.Desktop.StartupRegistration.BuildCommand`): the command is a quoted
  path to the new executable plus `--start-in-tray`; an entry recorded against the old
  folder is reported `StalePath`; the user's opt-in is not silently deleted; and
  re-registering writes the command for the new folder.
- A shortcut can be re-pointed at the new folder and keeps `--start-in-tray`. The check
  copies a shortcut inside the evidence directory; the Desktop is never touched.

## Integration records

The shipped Claude Code installer records an absolute path to
`integrations/claude-code/bridge.mjs` in the user's hook settings. The check writes that
record — in the evidence directory, never in the user's settings — pointing at the
*previous* folder, and reports whether the candidate still ships the same relative path. It
does: an upgrade does not rewrite the record and an archive extraction cannot, so a bridge
must be re-installed (or re-bound) after the move
(`integrationRecord.recordedPathFollowsPackageMove = false`).

## Rollback

Two routes, both exercised by the check.

1. **Older build on newer data** (tested). Start the previous build once against the data
   directory the candidate wrote. If it loses anything, the check reports
   `rollback.verdict = "NOT compatible: ..."`, lists the lost JSON paths and exits 1. Use
   this route only when the check said it held.
2. **Pre-update backup/restore** (tested, preferred). Stop the app and replace the working
   data directory with the backup from step 2 above. The check deletes `config.json` and
   `bridges/` from a candidate-written copy, restores the backup over it, and then
   requires `config.json`, `state.json`, the spool records and the recordings to match the
   pre-update data exactly (`rollback.backupRestoreVerified`).

That restore is a file-level replacement of a stopped instance; restoring while the app is
running is not tested, and neither is rolling the application folder itself back: there is
no uninstaller and no in-place downgrade, so keep the old folder until the new version is
accepted.

## Exit codes and evidence

`0` means every check passed. `1` means at least one check failed, **including the display
deferral**: with no non-primary display and no `-AllowVisibleChecksOnPrimary`, the windowed
comparison is not run, no process is launched, deferred evidence is written, and the run
exits 1.

The evidence JSON records the pass/fail list, the compared paths, every difference, the
normalization justifications, the rollback verdict, the spool comparison, the startup and
shortcut results, and a `checksNotPerformed` list. Read the evidence, not the exit code alone.

## Not verified

- The real desktop shortcut and the real `HKCU\...\Run` entry (a copied `.lnk` and a
  file-backed fake registry store are used instead), plus HUD rendering, focus, tray
  interaction and audible voice playback (both runs are `--hud-smoke` only).
- An in-place upgrade over an installed folder, a data directory moved to a different
  absolute path (producer-recorded absolute paths such as `health.json.directory` keep
  pointing at the copy they were written in), and live bridge rebinding on a real host: a
  stable spool proves the data survived, not that a real Claude Code or dsh hook fires.
