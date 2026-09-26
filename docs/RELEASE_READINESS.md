# OVERSEER: repository diagnosis and release-candidate plan

Planning baseline: `075356e`, branch `ux/first-use`, 2026-09-26.
Status: proposal and implementation handoff; no product changes or public release
authorized by this document alone. User requested a concrete diagnosis and plan.
Product name: OVERSEER. Project name: THE OVERSEER PROJECT.

## Assessment

The existing four-project architecture is suitable for a focused release iteration.
Core normalizes observations; App composes monitors and Windows services; UI projects
state into WPF; Tests exercise all three. Keep these boundaries and the current stack.
The main deficits are presentation hierarchy, tests across production boundaries,
distribution of optional integrations, and evidence from clean Windows environments.
This is not a recommendation to rewrite the monitor or introduce a new UI framework.

Latest recorded evidence (not rerun for this planning pass): 668 Release tests,
0 build warnings/errors; source package ad35e6d; 17 package checks with the normal
HUD lifecycle block skipped. Two live native UI observations after replacement
showed fresh project/task updates. Those observations cover this machine only.

## Confirmed findings and code ownership

| Finding | Evidence | Planned action |
| --- | --- | --- |
| Duplicate status and excessive permanent metadata | UI/HudWindow.xaml: SummaryRegion, StatusButton, ExpandedDragRegion, ActivityLegend, MetaStripLine, VoiceStatusLine, FooterHint | One summary; immediate project list; secondary details on demand. |
| Timeline duplication | TaskRowTemplate always binds TaskBins; ProjectRowTemplate always binds Bins | Explicit History disclosure; do not alter recording or interval semantics. |
| Technical/inaccurate copy | UI/ViewModels/HudText.cs always appends the Codex in-process limitation except when both capabilities are LiveObserved; healthy empty says no Codex conversations. HudViewModel.TaskViews.cs tests unprefixed reasons, while composite prefixes them with source identity | Use typed/source-aware health projection, concise display copy, separately accessible diagnostics. Do not strip uncertainty. |
| Project rows default expanded | ProjectRowViewModel initializes true; HudViewModel stores collapsed project IDs in memory | Define stable first-appearance defaults and once-per-attention-episode reveal. Do not repeatedly override a user's collapse. |
| Responsive behavior is dispersed | HudWindow.xaml.cs UpdateResponsiveLayout hides branding/legend at 260/300 content DIPs and sets 72/86 header height; HudChromeAdapter and controller WindowView partial own native geometry | Reflow content and centralize presentation thresholds; preserve native resize, Snap and hit-test ownership. |
| Packaged survival is not end-to-end correctness | Diagnostics/HudFixtureRunner.StartSmoke injects SyntheticMonitorSource directly; Test-PackagedRelease.ps1 normal mode checks process/window/close/restore but does not assert applied task state | Add packaged scenarios through real composition, readers, composite and controller, checking task/attention changes after startup. Retain synthetic renders. |
| Isolated normal runs conflict | Desktop/SingleInstanceGuard uses one fixed Local mutex; ShowRequestSignal hashes data-directory scope; App.OnStartup combines the two | Give lock and restore event one canonical per-user/data-directory identity; regress same-scope and different-scope launches. |
| Optional integrations are not distributed | Publish-Release.ps1 copies published app, docs, notices and example tones; actual 492-file build manifest contains zero integrations/ files | Ship explicit bridge payloads and setup/removal instructions, with versions and Node prerequisites. Never require a repository checkout. |
| Upgrade identity is weak | App.csproj and publish default use 1.0.0 across many packages; publish validates FileVersion.StartsWith(Version) | Separate numeric file/assembly version from release/informational version and source commit; support RC identifiers explicitly. |
| Download trust is unqualified | Get-AuthenticodeSignature of ad35e6d apphost returns NotSigned | Choose signing/distribution before stable public launch; test an actual browser download with standard protection intact. |
| Release docs disagree | README describes private Codex-only baseline and older test/package counts; UI_SPEC is still a placeholder; ledger's current block contains superseded counts/preservation claims | Replace current summaries and user guide; label historical evidence by revision. |

Paths in the table are under src/TheOverseer.* unless prefixed otherwise.
No product LICENSE file or CI workflow is tracked at this baseline. Distribution
terms and branding redistribution rights require an owner decision before publishing;
do not infer a license from upstream MIT notices or invent rights to supplied assets.

## Proposed UI contract

Goal: identify attention, current work and its project at a glance; inspect history
and diagnostic detail without making them the default reading path.

1. Pinned header: product identity, direct light toggle, recognizable mute control,
   expand/collapse, maximize, menu and close. Keep accessible names, keyboard focus,
   drag background, ordinary pointer over controls and the existing close-to-tray rule.
2. One summary: attention first, working second. Preserve unknown and lower bounds
   independently (e.g. `1+ needs you`, `2+ working`); do not claim complete zero from
   limited detection. Summary selections open Now with attention/working filtering,
   using NeedsHuman as well as blocking states. Mark the active filter and provide
   an obvious All reset. Summary counts still describe all monitored work.
3. One view selector for Now / Recent / This session, preserving persisted settings.
   Recent hours remain explicitly selectable rather than a hidden cycling gesture.
4. Projects ordered attention, working, recent with deterministic identity tie-breaks.
   First appearance: expand attention/active projects; collapse settled projects.
   This refines the earlier sketch: collapsing all active projects hides the main
   reason to use the expanded HUD. Preserve user choices during this session.
   A NEW attention episode may reveal its project once without stealing focus,
   changing the selected view, or jumping scroll while the user is browsing history.
   For hidden/capped attention, the summary must lead to a filter that exposes it.
5. Task row: title; harness plus one concise action/status; quiet turn elapsed time.
   Async NeedsHuman while working must remain distinct from blocked NeedsInput.
   Task click keeps its existing navigation action; a separate History control
   reveals history without activating the task. IDs, evidence and full source
   identity remain available through details/copy actions, not repeated on every row.
6. History disclosure per project: one project-hour strip; optional task strips
   inside that history area. Legend and time axis live there. Unknown intervals
   remain distinct from confirmed idle; partial/estimated intervals keep their labels.
   Closed history stops unnecessary geometry projection, never observation/voice.
7. One clickable health control in the pinned summary area, not below a long list.
   Concise labels: Connecting, Live, Limited coverage, Monitoring offline. Details
   list each source, freshness, supported/observed capabilities and a useful next
   action. Missing source, installed-but-idle, stale, unsupported and unreadable
   are separate states. A complete outage is prominent and cannot appear as idle.
8. Neutral rows with small state accents. Strong color for attention; retain text
   and icons so color is not the sole cue. Preserve optional right-side resizable
   light and saved widths (6..128); hide redundant left accent when light is on.
   Remove duplicate headline, decorative linework, constant voice sentence and
   permanent interaction instructions. Voice failure remains discoverable and
   visible when notifications were explicitly enabled.
9. One scrolling content area below the pinned controls. Content-width thresholds
   account for the status light; narrow rows wrap secondary metadata, not shrink
   all text with Viewbox. Begin with existing minimum sizes. Test 300/360/480/720
   content DIPs, short and tall heights, both themes and 100/125/150/200% display
   scales. Proposed readable targets: 13-14 DIP body, >=12 secondary; adjust compact
   minimum geometry explicitly if controls cannot fit. No horizontal scroll.
10. A missing harness presents a concise setup action in Projects & sources, not an
    empty page or generic crash. Preserve user's settings, mute and source roots.

## Iteration 1: simpler HUD and trustworthy candidate verification

One implementation goal with one review, internal checkpoints rather than separate
human-managed tasks:

- Render candidate states first using existing SyntheticHudScenarios/HudFixtureRunner;
  inspect working, blocking input, async input while working, failure, healthy empty,
  partial zero, offline, long names, many projects and mixed-source cases.
- Implement the UI contract in existing templates/view models. Add small view-model
  partials only if useful; no new project or presentation framework.
- Correct source-aware health wording. Keep raw reason codes out of the main HUD.
- Align instance/restore scope and add production-composition packaged scenarios.
  Isolate Codex roots, bridge roots, config, named objects and logs from the real HUD.
  Exercise known working -> input -> answer -> completion plus unavailable -> recovery.
  Assert freshness advances, expected IDs/states reach the UI, and no false offline latch.
- Test header hit regions after reflow, one-scroller last-row reachability, stable
  focus/expansion, unknown counts, action-independent timers and history disclosure.
- Update user-facing docs and changelog. Commit tested source before publishing.
- Produce a versioned internal RC with checksums and build manifest; do not call
  17 partial smoke checks a fully qualified release. Skips remain explicit.

## Iteration 2: release distribution and compatibility qualification

- Keep a self-contained win-x64 ZIP initially. Proposed public support: tested,
  currently supported Windows 11 x64 versions, standard user, no SDK required.
  Do not promise every Windows edition, ARM64, WSL-hosted app, macOS or Linux.
- Ship opt-in dsh/Claude assets, notices, setup prerequisites, preview/apply/remove
  instructions and compatible host versions. Install paths must survive app updates;
  either use a stable per-user bridge location or an explicit tested rebind workflow.
  Existing hooks/profile settings must survive installation and removal unchanged.
- Provide source setup states and explain required restart/reconnect if bridge
  discovery remains startup-only. Verify before claiming hot discovery.
- Compare old/new packages using COPIES of fixtures/config/state, not the user's
  real settings. Verify defaults, custom voice clips, mute/provider, roots, project
  aliases, window placement and close behavior survive upgrade. Verify rollback
  or document the precise supported rollback boundary. Test shortcut/startup targets
  after moving between versioned package folders; no dangling old executable path.
- Make first-run voice usable without a developer's installed pack: system voice
  when available, muted until chosen; explicit no-voice fallback if unavailable.
  Test audible notification, instant mute, no startup backlog replay and no chatter
  for user-driven actions except an explicit preview or opted-in startup cue.
- Qualify each advertised harness on a live installed version, including attention
  and completion where observable. Until this passes, label that integration
  experimental; decide explicitly whether to delay broad three-harness support.
- Test the exact downloaded archive on a clean Windows VM and an independent
  physical machine/account. Record OS build, scale, harness version, hash and
  pass/fail/blocked results. No dev SDK, checkout, cached packages or author paths.
- Resolve signing/distribution and product redistribution terms. No security-policy
  bypass instructions, account creation, purchase, remote upload or publication
  is authorized by this planning packet. Prepare local artifacts first.

## Release acceptance matrix

| Gate | Required evidence |
| --- | --- |
| Start | Browser download -> extract -> run as standard user, no .NET SDK/runtime installation required; test native dependencies on clean OS. |
| Empty machine | No harness installed: usable HUD and setup guidance, no crash or fabricated work. |
| Monitoring | Known live tasks and source failures update the actual HUD after startup, not just debug output. |
| Attention | One blocking and one asynchronous request; correlated answer clears correctly where observable; unavailable detection stays disclosed. |
| Harnesses | Per-version live evidence for every integration advertised as supported; installation/removal preserves unrelated configuration. |
| UI | Both themes, compact/expanded, narrow/tall/short, light at maximum width, keyboard, last-row access, native Snap, drag/control separation. |
| Lifecycle | Same-scope second launch restores; different isolated scope cannot touch user HUD; hide, restore, explicit graceful Exit, startup opt-in. |
| Recovery | File locks/malformed data, sleep/wake, monitor removal/reconnect, stale -> fresh, source disappearance/recovery. |
| Voice | Real audio in clean profile, missing device/voice handling, mute persists, no duplicate/replayed notifications. |
| Upgrade | Previous -> candidate -> supported rollback with saved settings and valid shortcut/startup/integration paths. |
| Distribution | Unique version/source hash, private-payload audit, bridge assets/notices, correct signing status, tested download path. |

Candidate qualification cannot pass with an unrun required gate. Distinguish local
automated checks, physical checks and external-machine checks. CI can automate build,
tests and package inspection but is not evidence for an interactive tray/audio session.
Add a small Windows CI job only after the local commands are stable; no secrets needed
for build/test. No auto-publishing workflow in this scope.

## Release decisions and constraints

Two iterations are a reasonable work structure, not a guarantee that platform or
upstream limitations disappear. Freeze additions until these gates pass. Retain
the current monitoring budgets/privacy model, source-local health, one notification
coordinator and the explicit StartAsync/Completion lifecycle. Never weaken bounds
or suppress coverage warnings simply to make the UI look cleaner.

External needs: access to a clean Windows environment, an independent test machine,
actual dsh/Claude sessions, and an owner decision about distribution/signing/terms.
Work that does not depend on them can proceed when implementation is authorized.

## References

- [NN/g progressive disclosure](https://www.nngroup.com/articles/progressive-disclosure/): put frequent tasks up front and make detail access explicit.
- [Windows responsive layout](https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design): adapt to available app-window space.
- [.NET publishing](https://learn.microsoft.com/en-us/dotnet/core/deploying/): self-contained bundles .NET, not all operating-system/native prerequisites; runtime updates require republishing.
- [Windows download reputation](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation): signing and reputation affect the actual download experience; signing alone is not a universal no-warning guarantee.

Frozen AGENTS.md and V1_2_IMPLEMENTATION_SPEC.md are unchanged. This is the user's
post-V1 usability/public-release direction, not a retrospective rewrite of V1.
