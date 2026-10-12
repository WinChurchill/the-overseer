# Automated private qualification

Run on an owned test environment. Preserve installed releases, personal profiles
and source sessions. Generated evidence belongs under ignored `artifacts/`.
Use a short evidence path such as `artifacts/qualification` (the default). Deeply
nested extraction paths can make the Windows native WPF loader fail with
`0x800700CE` (filename or extension too long), even though the ZIP is valid.

1. Obtain the private packaging workflow artifact. It includes the release ZIP,
   `.sha256` and `build-manifest.json`. Record its workflow URL and source commit.
2. Run the existing environment/build checks from CONTRIBUTING. Then qualify the
   exact archive, without republishing or rebuilding it:

```powershell
./scripts/Test-AutomatedQualification.ps1 -CandidateArchivePath <CI-ZIP> -BuildManifestPath <manifest> -PreviousArchivePath <previous-ZIP> -CiRunUrl <workflow-URL>
```

The runner verifies archive/embedded source identity, reuses the pre-user package,
scaling and upgrade checks, and runs a 3600-second hidden mixed-source stress check.
It writes resource budgets before measuring. Defaults bound reads to 4 MiB/pass,
correlated requests/attention history to 64 entries and history to one hour.
Additional conservative regression gates are 384 MiB private memory, 96 MiB
growth after 60 seconds, 128 additional handles, 16 additional threads, average
25% of one CPU core, 64 MiB owned evidence/data and 15 seconds per transition.
These gates are qualification limits, not a performance promise on every machine.
No forced termination qualifies Exit. A short `-EnduranceSeconds` control run
does not meet the full-hour gate. Synthetic stress does not qualify normal real-host use.

Package qualification checks the synthetic HUD's saved state and its changed
theme/opacity, in addition to its exit code. Use at least nine seconds for
`-HudSmokeSeconds`; a shorter run cannot qualify persistence. A zero exit code
with missing or unchanged settings is a failure. Preserve failed evidence and
record the launch context before retrying: RC19's prepared installation failed
atomic temporary-file writes under an escalated diagnostic launch, while ordinary
managed launches saved and reopened correctly. No filesystem permissions were changed.
The pre-user runner bounds MSBuild to one worker and disables reusable build workers
for both build and test; this avoids unbounded fan-out on large developer machines.

Windowed tests use a secondary display. On a deliberately idle test desktop,
`-AllowVisibleChecksOnPrimary` permits non-activating checks on the primary display.
`-IncludeActivationChecks` enables the pre-user runner's intentional activation checks
on the selected secondary display; it does not opt into the primary display.
Actual menu clicks remain separate. The hidden soak substitutes
only invented producers, startup/navigation/clipboard and tray availability; it
uses the production composition/readers/controller/view model/history/voice policy.
It does not establish native tray, visible UI or exact-chat focus behavior.

For a clean Windows machine, copy `Test-CleanMachinePackage.ps1` and download the
CI ZIP through a browser. Run the helper from Windows PowerShell with the exact
SHA256. It needs no development toolchain. Keep protections enabled and leave
security/authentication prompts to the owner. Test empty setup/save/reopen/explicit
Exit before adding Node or host integrations. Record standard-user/clean-OS status;
absence from PATH alone is not proof of a pristine environment.

Real Codex, DSH Desktop and Claude Code engine checks need separate evidence of
working, question/reply, permission/resume, completion/error/abort and restart.
Codex approvals remain Unsupported. Do not substitute terminal DSH for Desktop,
synthetic hook invocation for an engine, or accepted URI dispatch for correct focus.
Physical monitor/power/logon behavior, audible usefulness and publication decisions
remain owner checks wherever the environment cannot establish them.

The private workflow has read-only repository permissions, emits unsigned RC
artifacts and creates no release/tag/site. Its branch-only push bootstrap is limited
to changes to that workflow; manual dispatch becomes available after reviewed merge.
Qualifying an RC does not approve publication. A final version/signing change alters
shipping bytes and requires a new archive identity and package qualification.
