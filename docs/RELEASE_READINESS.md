# OVERSEER release readiness

## Current pre-publication plan — 2026-10-05

Status: **RC13 READY FOR OWNER RETESTS.** RC12 preserved.
Public qualification and publication approval remain pending.
Baseline: reviewed local main `fcabe0033e6d5b04a10293e2f70de26625654dd6`.
Planning/review branch: `fix/prepublication-polish`.
Private source staging is now authorized for
[WinChurchill/the-overseer](https://github.com/WinChurchill/the-overseer).
The owner chose full history with only Astra/DeepSeek identities replaced by his
verified Git identity in the upload copy; original local history remains intact.
See [private staging](PRIVATE_GITHUB_STAGING.md). Source upload is complete; hosted
CI is being retested after its first run exposed a resize fixture requesting height
810 on a desktop that limits it to 788. The fixture now fits the host work area and
also asserts persisted height; application code and RC13 bytes are unchanged. Private
upload does not approve public visibility, release assets or website deployment.
Owner selected **GitHub + a project website**. Proposed hosting: GitHub Pages,
with a small static site in `site/`; review it locally before deployment.
The execution packet is ignored `.agent/HANDOFF.md`; source status is
[IMPLEMENTATION_STATE.md](IMPLEMENTATION_STATE.md).

The diagnostic-report pass corrects live Codex chats disappearing from Now after
an output-only envelope, shares display numbers across harnesses and polishes the
HUD. Large classes are split into cohesive files. All 34 `catch (Exception...)`
sites were reviewed: most have typed recovery and an observable result; the four
unfiltered catches already report a safe error. Silent source reconcile/dispose
and speech cleanup now emit fixed diagnostics. Voice import/preview/status failures
also report fixed codes; arbitrary exception messages are omitted from reports.
Pure validation and cancellation stay quiet; consumers report their result.

VoiceSmokeRunner and HudCompositionCheckRunner remain opt-in shipped diagnostics:
qualification must exercise the downloaded executable and real composition. Normal
startup does not invoke them; synthetic data and isolated settings protect live hosts.
Light colours share the same state meaning in both themes; working is green,
attention/error red, uncertainty amber. Brighter dark metadata aids readability.
Only scratch inventory is authorized here: candidate ZIPs, backups and evidence
must survive. No blanket artifacts or .tools deletion is part of qualification.
Public release packaging still requires a hosted CI run of the approved revision
and CI-produced bytes; local RC folders are private qualification only. The existing
Verify workflow has no public-release upload step. Publication remains a later action.

End state: one reviewed source revision, one final archive with a recorded hash,
complete automated and human qualification, accurate public docs/site, and a
ready-to-publish GitHub release. Making source, release assets and website public
requires the owner's final approval of that concrete packet.

### Current RC13 qualification — 2026-10-05

Clean source `c0ced607ff5ab9ab7df9e10f7c0ea13eff5cde67`; private candidate **1.0.0-rc.13**.
[RC13 ZIP](../artifacts/prepublication/rc13/OVERSEER-1.0.0-rc.13-win-x64.zip),
[owner handoff](../artifacts/prepublication/owner-handoff/OWNER_HANDOFF.md),
[six-area report](../artifacts/prepublication/rc13-checks/run-20261006T004243Z-9d6995c0/pre-user-report.json).
SHA-256: `45A9F3F2D91B65DCDC91F25A896EEE49FF29B9DE55F49EC4BFBA71DF7B0E27AA`; 76,915,148 bytes;
591 payload / 90 integration files; unsigned self-contained x64; .NET/Desktop 10.0.12.
Clean-source build: 0 warnings/errors, 749 tests pass/0 fail/skip; DSH 39, Claude 29.
Exact ZIP: 42 pass/0 fail/3 intentional activation deferrals; integration 17/17,
composition 42/42, 48 renders; no focus takeovers in 546 foreground samples. Scaling
and RC12 upgrade/rollback pass in HUD/Window (25 checks each). Candidate history
(202 commits), source export and payload scans clear; site 85 checks/12 controls pass.
Read-only corrected-source observation confirmed a formerly missing active Codex
chat is fresh and appears in Now after hydration. Full H3 remains pending.
Fresh RC13 folder/shortcut uses separate settings; prepared without replacing RC12.
All 16 owner rows are PENDING against the new source/hash. RC12 owner kit is preserved
under owner-feedback/rc12-owner-kit. Same-chat review: PASS WITH NONBLOCKING NOTES
for local retest; independent review and all public/real-host/physical gates remain pending.

### Prior RC12 DSH question follow-up — 2026-10-05

The owner confirmed real permission escalation/resume with the installed DSH observer.
Observer 1.1.0 now recognizes blocking questions and reads the inspected Desktop
0.2.0-rc.2 native cache for timed questions. Pending results and completed turns
retain answerable requests; correlated admitted replies settle them. Current-state
recovery restores pending IDs silently. Missing/stale/changed/oversized metadata
reduces coverage; no waterfall, answer or conversation content is intercepted.
The version-specific native cache is an explicit compatibility limit. Real question
qualification remains pending even after the isolated installed-code contract check.
The HUD mark now says `question pending` for an unanswered non-blocking request.
An older app-copy item remains unanswered in raw metadata, but its information is
superseded and the later live HUD check showed this chat without attention. Turn
completion also clears Codex turn-scoped requests; raw unanswered metadata alone
does not prove current attention. No answer was fabricated. Claude's 15 hooks emit
work/completion records and its row is visible; input/permission, audible and
clean-machine qualification remain pending.

That prior replacement was **1.0.0-rc.12**, clean source
`68c2d6190698c9821179b025441a21dbf6490a4e`.
[RC12 ZIP](../artifacts/prepublication/rc12/OVERSEER-1.0.0-rc.12-win-x64.zip),
[archived RC12 handoff](../artifacts/prepublication/owner-feedback/rc12-owner-kit/OWNER_HANDOFF.md),
[automated report](../artifacts/prepublication/rc12-checks/run-20261005T232402Z-d4134442/pre-user-report.json).
SHA-256: `8EF59BFAC561E6349DE984039E9F64C5F04F4432A328720B1A683AD28A2A6342`;
76,905,615 bytes; 591 payload / 90 integration files; unsigned self-contained x64.
Build: 0 warnings/errors; 741 tests pass/0 fail/skip; DSH 39/39, Claude 29/29.
Exact ZIP: 42 pass/0 fail/3 deliberate activation deferrals; integration 17/17,
composition 42/42, 48 renders; no focus takeover/547 samples. Scaling and RC11
upgrade/rollback pass in both modes. Candidate history (198 commits), source export
and exact payload secret scans clear; site 85 checks/12 publication controls pass.
All new H1–H16 rows remain PENDING against this hash. RC11 and its owner kit are
preserved under owner-feedback/rc11-owner-kit; the evidence below is historical.
The RC12 shortcut uses separate writable test settings and explicit real producer
roots; live launch and saving were verified. Normal-data agent launches reported
a save failure, so ordinary owner-path persistence remains part of H11/H14.
Normal settings were preserved. Empty/Live setup passed stock Windows PowerShell;
the helper rejects an older ZIP before writing. Audible/live-host checks remain pending.

### Prior RC11 owner feedback corrections — 2026-10-05

RC9 owner reports: Preview has no playable recording; DSH absent; Recent time control
uses the wrong dark palette; independent Work chats use prompt-derived project names.
The selected pack is missing; explicitly use Windows speech (silent synthesis verified).
The observer is now installed in the actual Desktop profile after an owner-coordinated
quit, with private backups; it produces fresh status and no writer errors after relaunch.
Real Desktop task/approval/resume behavior still needs retesting after OVERSEER restart.
The Recent control and standalone managed-workspace grouping are corrected in source.
Live validation also found DSH input capability was overstated; it is now explicitly
Unsupported. Current task coverage is complete, with a disclosed late-attach history gap.
RC11 review found an older unanswered optional question. The subsequent live check
showed this chat without attention; full H3 and independent-machine checks remain pending.
The verified replacement is **1.0.0-rc.11**, clean fresh source `8b1d3454179d10fb3f389e2097425883d1803413`.
[RC11 ZIP](../artifacts/prepublication/rc11/OVERSEER-1.0.0-rc.11-win-x64.zip),
[preserved RC11 retest handoff](../artifacts/prepublication/owner-feedback/rc11-owner-kit/OWNER_HANDOFF.md),
[automated report](../artifacts/prepublication/rc11-checks/run-20261005T220959Z-d572c12b/pre-user-report.json).
SHA-256: `FF8F38446407AD26ACC6DCD9B7898C735C27A24B428FF9BAB5371569FC9A1FFB`.
Build: 0 warnings/errors; 727 tests pass. Exact package: 42 pass/0 fail/3 focus
activation deferrals; integration 17/17, composition 42/42, 48 renders, no focus
steal/547 samples. Secondary-display scaling and RC9 upgrade/rollback pass in both
modes. Source/history/payload secret scans clear; site 85 checks/12 controls pass.
RC9/RC10 artifacts remain fixed; the RC9 results below are historical evidence.
All RC11 owner H1–H16 remain pending until observed and recorded against this hash.

### Executed RC9 preparation and owner handoff — 2026-10-05

- Tested candidate: **1.0.0-rc.9**, unsigned private ZIP, source `61d7d078cb39e58b1fbeb55bba220853d65ed491`.
  [Application ZIP](../artifacts/prepublication/rc9/OVERSEER-1.0.0-rc.9-win-x64.zip),
  [preserved RC9 packet](../artifacts/prepublication/owner-feedback/rc9-owner-kit/approval-packet.json),
  [preserved RC9 results](../artifacts/prepublication/owner-feedback/rc9-owner-kit/OWNER_CHECKS.csv).
- SHA-256: `E9C27D69BFC2729FD89F266C9E3A9CA9FD6B89E53E17C310D55547A2253C8FA1`. These hashes identify the preserved RC9 run; new owner results must name the replacement hash.
- Fresh-clone .NET build/test: 0 warnings/errors, 715 pass, 0 fail/skip. DSH 30 and
  Claude 29 pass; extracted bridge tests pass too. NuGet advisory scan found no
  vulnerable packages; bundled .NET/Desktop runtime 10.0.12 matches the current patch.
- Full-duration package: 42 pass, 0 fail, 3 deliberate activation deferrals;
  integration 17/17, composition 42/42, 46 fixture renders. No focus takeovers in
  547 samples. Secondary-display scaling pass; RC8 upgrade/rollback 25/25 in each
  HUD/Window mode. Report/comparison/placement controls pass.
- Gitleaks 8.30.1, checksum verified: all reachable local history (191 commits),
  committed tree and exact extracted payload found no secrets. This is a scanner
  result, not proof of absence. Current ledger personal path is redacted; original
  history and three commit-author identities remain for owner disclosure review.
- New public README/community/privacy/support files and a three-page static site.
  85 local site checks and 12 publication controls pass. Mobile 390px navigation,
  768px layout, desktop images/keyboard skip link and sampled contrast passed.
  Real canonical/download/security-report URLs and search indexing await publication.
- Five-minute synthetic Codex idle/work/missing/recovery observation: 297 samples,
  17.96 MB peak private memory, 53.57 MB peak working set, 1.30s CPU.
  Work resumed after source restore; watcher failure remained honestly degraded to
  polling. Restart to restore the watcher. This does not satisfy H15's one-hour test.
- DSH Desktop 0.2.0-rc.2 metadata rechecked. Prior Claude Store package was not
  rediscovered; H5 must record the actual current app/Code engine and environment.
  No live host settings, live app, startup registration or primary display changed.
- Same-chat review: **PASS WITH NONBLOCKING NOTES** for local handoff, not independent
  review or public release approval. No remote/upload/deployment/public tag created.

For the current RC13 retest, start with the owner retest handoff. The optional
[setup helper](../artifacts/prepublication/owner-handoff/Start-OwnerCheck.ps1) verifies
the RC13 ZIP, extracts into a spaced/non-ASCII folder and keeps disposable test settings.
It passed stock Windows PowerShell and a wrong-ZIP/no-write control. Default run
prepares only; `-Launch` deliberately opens the app. Empty and Live use separate data.
The Live mode reads default producer spools but stores its own app preferences.

Complete H2/H3, opt-in bridge setup and H4–H15 locally; perform H1/H16 in a clean and
independent environment. H1's staged browser download and H16's public-link portion
wait for the configured GitHub route. Do not mark either whole item passed from a
local file copy or developer-machine run. Hosted CI needs the chosen GitHub repository
and authorized private upload; final public history/signing/security-route decisions
can be settled alongside the owner tests. All H1–H16 rows start **PENDING**.

### Original planning preflight (historical; superseded by execution above)

- At planning time no Git remote, project website, public URL or hosted CI run was configured.
- Local baseline: 715 .NET tests, DSH 30 and Claude 29; full-duration private
  packaged smoke 42 pass / 0 fail / 3 activation checks deferred. These are old
  revision-specific results, not certification of a future release.
- RC8 predates remediation. Newer archives cover different checks; no single
  final public candidate has all qualification evidence.
- README has a brand-only title and no screenshots. Relative Markdown links
  checked in the preflight resolve locally; public download targets are absent.
- CONTRIBUTING, SECURITY and issue templates are absent.
- A personal Windows profile path occurs in IMPLEMENTATION_HISTORY.md.
  Limited tracked-text private-key/token patterns found no matches; this is
  **not** a full secret/history scan. Review intended public history before upload.
- Optional configured TTS sends generic notification text, event code and task
  alias to its endpoint. Public privacy claims must distinguish this opt-in
  network path from local monitoring and offline speech.
- DSH Desktop and the Claude desktop app were found on 2026-09-29. Recheck actual
  versions and session execution environment; neither bridge has live qualification.
- MIT code/docs/generated artwork and upstream attribution are resolved.
  Codex approval detection remains explicitly Unsupported by owner choice.
- Preflight record: `artifacts/prepublication-planning-20261005/preflight.json`.

### Ordered execution

| Step | Work / owner | Required result |
| --- | --- | --- |
| P1 — Source and disclosure cleanup | Coding agent; owner resolves any disclosure/signing choices | Audit intended public tree, main's reachable history and selected tags with a pinned secret scanner; scan extracted payload separately. Inspect personal paths, real logs, screenshots, commit metadata, upstream notices and tracked binaries. Redact scan reports. No credential/third-party/private data finding left unresolved before upload. Preserve private evidence and local history; never rewrite main or force-push. If public history needs a different export, obtain a specific decision. |
| P2 — Product and build qualification | Coding agent, then review role | Review monitoring/state boundaries, source failure/recovery, record bounds, privacy, voice anti-spam, lifecycle and risky-path tests. Remove reachable dead code/stale examples only after checking consumers. Audit top-level/transitive NuGet dependencies and bundled .NET runtime versions; Node bridges have no npm dependencies. Fix material findings, add meaningful regressions and rerun affected/full checks. Current build instructions work from a fresh clone; hosted Windows CI passes for the final source. |
| P3 — Live desktop integrations | Coding agent prepares isolated previews/backups; owner operates app sessions | Recheck DSH Desktop's bundled manager and dedicated desktop profile, and Claude Code desktop/editor Windows-local settings/runtime. Install from permanent bridge directories. Record real work, input/approval, resumed work, completion/abort and host failure/recovery. Remove/rebind/reinstall without changing unrelated settings. Record host/engine versions, spool/source identity and observed latency. Advertise only qualified environments; WSL/SSH/cloud remain out of scope unless separately qualified. |
| P4 — Public docs, website and SEO | Coding agent; owner approves presentation | Rewrite README around a real download, quick start, truthful capability matrix and sanitized screenshots. Add contribution/build guidance, support/bug template and a usable private security-report route. Reconcile state/readiness/upgrade/scaling/UI docs; keep old evidence historical and ignored artifacts private. Build home, install/troubleshooting and privacy pages; validate responsive/keyboard use, links, version labels and SEO preflight below. No deployment yet. |
| P5 — Final packaging and repeatable evidence | Coding agent | First qualification candidate: next unused RC identity (currently rc.9). Run existing source, bridge, control, packaged, upgrade/rollback, scaling and silent-voice checks sequentially. Collect soak/resource measurements for idle, multi-host work and recovery. Review each report's omissions, not just exit codes. Select signed or explicitly unsigned distribution before final packaging; if signing, integrate it before hashes/manifest/ZIP. Build the final proposed public version from clean reviewed source; qualify those exact final bytes. Any change to shipped bytes invalidates the hash-bound signoff. |
| P6 — Human and clean-machine signoff | Winston; agent prepares inputs and records results | Complete H1–H16 below against the final archive. Require a clean Windows environment without SDK/.NET/Node/harness and an independent machine/account. Open failures become fixes and focused retests; rerun the full release suite after product changes. No skipped physical/live test becomes a pass. |
| P7 — Review and approval packet | Review role; Winston approves publication | Review complete diff against main. Close every blocker/major finding. Assemble source commit/tag, archive hash, manifest, support matrix, hosted CI URL, automated reports, H1–H16 results, signing status, README/site preview and draft release notes. The approved archive is fixed; previous RC/dev archives are not published as the new release. |
| P8 — Controlled publication | Agent after explicit owner approval | L1–L4 are preparation during P1–P7, with separate authorization for private uploads; L5 is the final public approval. Execute L6–L10 afterward. Public URL/download/indexing tests run after visibility/deployment and before announcements. Keep identities consistent; patches get new versions. |

Owner inputs when execution needs them: GitHub account/repository; acceptable public
history/disclosure scope; signed release or explicitly unsigned preview; any custom
domain (default is the GitHub Pages project URL). No branding-license reapproval needed.
Budget/identity requirements for signing are decided before starting that work.

### Exact owner checklist

The agent supplies a final ZIP, hash, disposable data/profile setup, expected results
and an evidence sheet. For each item record PASS / FAIL / UNSUPPORTED, OS/host version,
archive hash, date and a short observation. An unexplained omission blocks signoff.
Use a standard account and leave normal Windows protections enabled. Tests requiring
display changes, logon or intentional activation run in an agreed idle test session.

| ID | What Winston checks | Pass condition / existing gates |
| --- | --- | --- |
| H1 | Download the staged final ZIP in a browser on a clean Windows VM; verify SHA-256, extract to a path with spaces/non-ASCII text and launch as a standard user | Same final bytes, no SDK/.NET runtime/Node/repo/cache needed for the app; record Defender/SmartScreen/Smart App Control result and signing status. Do not disable protections to get a pass. Repeat public URL download in L7. R-01/02/05/26/27. |
| H2 | First run with no harness/config, then close/reopen | Usable empty/setup view, muted voice, honest unknown/offline health, no fabricated tasks and no unexpected setup writes. R-03/04/20. |
| H3 | Real Codex work: active turn, async question, partial/full answer, completion, abort and error | A live working chat appears in Now and contributes to Working after hydration; status/attention agree with the session and an answer clears its request. Codex approvals are visibly Unsupported and never inferred or answered. R-06/07/08/09. |
| H4 | Real DSH Desktop session through the desktop profile | Work -> question/choice wait -> answer/resume -> finish/abort; retain timed answerable questions after tool return/turn completion, quietly recover after OVERSEER restart, then clear on admitted reply. Repeat permission/resume and host restart. Record Desktop/observer versions; no terminal profile substitution. R-11. |
| H5 | Real Claude Code session in the desktop Code tab or editor, in its actual Windows-local execution environment | Hooks really fire; working/input/permission/resume/completion/error match; Node/path/settings resolve in that environment. Record app and Code engine versions. R-11. |
| H6 | Install, remove, move/rebind and reinstall each bridge using backed-up test settings/profile | Unrelated settings preserved, ordinary host use still works, no dangling bridge path, producer resumes from the kept directory and OVERSEER discovers it after restart. R-11/23/24. |
| H7 | Stop/restart one host and temporarily make its source unreadable in isolated test data | Coverage becomes honestly limited/offline, peer sources keep working, counts do not become a false healthy zero, recovery resumes without stale attention. R-07/19. |
| H8 | Both themes; compact/expanded; narrow/short/tall/maximized; filters, project grouping, history, light width, pin/mute/settings | Nothing clipped, inaccessible or contradictory; 300x220 minimum layout remains usable; history quality/unknowns remain disclosed. Check shared numbers, ellipsis, aligned columns/dividers, History hover/keyboard focus, dark Recent menu, mic/light state and + explanations. R-12/14/15. |
| H9 | Pointer drag, edge/corner resize, maximize/restore, hover/keyboard Snap, Tab/Enter/Space, Alt+Space and scroll | Native operations and explicit controls work; controls do not accidentally drag the window; geometry restores correctly. R-13/14. |
| H10 | Move across 100/125/150/200% display scales, detach/reconnect a monitor, sleep/wake | Reachable placement, readable layout and resumed monitoring; no offscreen orphan or unsolicited focus takeover. Record actual tested scale/monitor combinations. R-14/19. |
| H11 | Tray Show/Hide, pin/mute/settings, close-to-tray, Exit, deliberate second launch and no-tray fallback | Intentional activation works, normal updates do not take focus, one scoped instance restores, Exit really stops it and saves placement. R-16/17. |
| H12 | Enable startup, sign out/in, move application folder and repair the recorded startup path, then disable startup | One intended instance starts; preferences/placement persist; startup entry reflects the actual path and opt-out removes it. R-16/24. |
| H13 | Offline speech preview and live notification; selected pack/provider if advertised; mute/restart/unmute; absent device/voice | Audible and appropriate cue, no duplicate/backlog spam, mute persists, failures stay visible. If TTS is advertised, test a configured endpoint and its failure while respecting documented privacy. R-20/21/22. |
| H14 | Upgrade from RC8 or the oldest explicitly supported build into a new folder using backed-up test data; restore backup to roll back | Settings, recordings, project mappings, disabled sources, placement and spools survive; shortcut/startup/bridge paths are repaired; documented rollback restores exact data. R-18/23/24/25. |
| H15 | Normal multi-host use for at least one hour; remove startup/hooks and delete the app folder afterward | No hang, focus theft or accumulating voice backlog; measured resources respect configured bounds; uninstall leaves unrelated host settings working and explains optional removal of remaining OVERSEER data. R-17/19/24 plus soak evidence. |
| H16 | Repeat core launch/setup/work/exit on an independent machine or Windows account; inspect public README/site/download/support links | Setup can be followed without your dev environment, screenshots and feature claims are accurate, privacy/support/signing limits are understandable. Record supported OS builds and host versions. R-28. |

R-10 extracted bridge-suite qualification is automated in P2/P5.
All R-01–R-28 gates map to P1–P8/H1–H16, with R-09 approval capability explicitly
Unsupported. For any unqualified optional provider/host, either finish qualification
or obtain an explicit scope decision and clearly restrict public claims; never silently
drop a required check. SEO/website/security checks are additional gates.

### Website and SEO preflight

- Descriptive title/H1: **OVERSEER — Windows HUD for AI coding agents**.
  Explain working/needs-you at a glance; disclose Codex approval limits next to claims.
- Suggested GitHub description: “A local Windows HUD for AI coding agents:
  see their work and when they need you.” Add relevant topics such as
  `windows`, `wpf`, `codex`, `claude-code`, `deepseek`, `ai-agents`,
  `developer-tools`, `monitoring`. Topics aid repository discovery
  ([GitHub guidance](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/classifying-your-repository-with-topics)).
- One clear “Download for Windows x64” action links to the versioned **app ZIP**,
  not GitHub's auto-generated source ZIP. Link Source, Install, Troubleshooting,
  supported versions, License/attribution, Privacy and bug/security reporting.
- Use sanitized current HUD screenshots in both themes. Label staged/synthetic
  illustrations. Add alt text, favicon and a social preview (1280x640, under 1 MB
  for GitHub); do not include private project/session identifiers.
- Semantic HTML and crawlable text without requiring JavaScript; responsive layout,
  keyboard access, useful focus indication/contrast; no broken local or external links.
- Configure unique titles/descriptions, canonical HTTPS URLs, Open Graph/Twitter
  metadata, sitemap and correct project-subpath asset/download links. Set robots rules
  only at an origin root you control: a project-subdirectory robots.txt is not an
  origin-wide robots policy ([Google robots guidance](https://developers.google.com/crawling/docs/robots-txt/create-robots-txt)). Keep drafts local and deploy only at the publication gate.
- Run browser/link/accessibility and SEO audits on the local built site; triage failures.
  After deployment validate HTTP status, canonical URL, sitemap and anonymous navigation.
  Submit the site URL/sitemap through Search Console after ownership verification.
  Google indexing/ranking cannot be certified before launch or guaranteed
  ([Google SEO guide](https://developers.google.com/search/docs/fundamentals/seo-starter-guide)).
- Site has no added analytics by default. Explain optional network TTS accurately;
  do not claim zero network activity for every user-selected provider.

### Exact publication checklist

| ID | Action in order | Required result |
| --- | --- | --- |
| L1 | Resolve account/name, intended public history, Windows/host support matrix and signed/unsigned distribution | All choices written in the approval packet. Full history/payload scans finish **before private upload**, not just before public visibility. |
| L2 | Create/configure private GitHub staging repository and origin when authorized; push vetted main/plan branch and only intended tags | Hosted Windows CI passes; required checks/review protect main as supported by the account plan. Do not push all local refs/artifacts. Pages remains disabled: a Pages site may be public even from a private repo ([GitHub Pages guide](https://docs.github.com/en/pages/getting-started-with-github-pages/creating-a-github-pages-site)). |
| L3 | Prepare final version/tag from reviewed source and the final package workflow; run all final-byte checks and H1–H16 | One commit/version/hash/manifest identity; no stale RC evidence substituted; signing is finalized before archive hashing. |
| L4 | Draft the GitHub release privately; attach final ZIP, SHA256SUMS, sanitized manifest/evidence summary, accurate release notes and install/upgrade instructions | Every attachment reviewed. Private browser-download clean-machine test uses these exact bytes; a draft is supported by [GitHub releases](https://docs.github.com/en/repositories/releasing-projects-on-github/managing-releases-in-a-repository). |
| L5 | Review local website, README/About/topic/social previews, support/security route and complete release packet with Winston | Explicit final approval to expose source/history, publish these assets and deploy this site. No earlier check implies approval. |
| L6 | Make vetted repository public, publish the reviewed release/tag, then enable/deploy GitHub Pages from only site/ | Download links and release version agree; expose no internal artifacts. Enable repository secret scanning/private vulnerability reporting where available; security response route works. |
| L7 | Use a signed-out browser and clean Windows account to download from the real public CTA, compare hash and launch | Public access, correct app ZIP, preserved bytes, actual browser/protection behavior and usable start. Record public URL/time; no developer permissions required. |
| L8 | Verify live website HTTPS, mobile/keyboard use, source/install/privacy links, social metadata, canonical and sitemap; update GitHub About URL | No launch-breaking link/HTTP/asset error. Verify Search Console ownership and submit sitemap; later indexing is observed, not assumed. |
| L9 | Announce only after L7–L8 pass, using owner-approved channels and copy | Link the site/release; accurately label supported/experimental/unsupported capabilities. Prepare copy locally; sending/posting needs explicit authorization. |
| L10 | Run a short launch support pass, triage reports and define next patch work | Reproduce bugs against version/hash, keep issue/security instructions current, never silently replace tested release assets; a patched binary gets a new version/hash. No recurring monitor is scheduled by this plan. |

Signing reduces distribution friction but does not guarantee absence of SmartScreen
warnings; unsigned builds may also be blocked by Windows policy. Record what the
clean-machine run observes and state the actual distribution limits
([Microsoft signing/reputation guidance](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/smartscreen-reputation)).

The packet is ready for public approval when P1–P7, H1–H16 and private preparation
L1–L4 have recorded results, no blocker/major defect or unresolved disclosure remains,
and the final bytes match the packet. L5 authorizes publication; L7–L8 are explicitly
post-publication checks that must pass before L9 announcements. Search indexing may
follow later and is not a promised launch result.

---

<details>
<summary>Historical planning, protocols and revision-specific evidence (preserved)</summary>

Current source status: [IMPLEMENTATION_STATE.md](IMPLEMENTATION_STATE.md).
Current packaged candidate: **RC8**, source `f14dc47`, unsigned; its recorded six-area
results are summarized in current state. Remediation source is newer than RC8.

Owner decisions on 2026-09-29: original code, docs and generated artwork use MIT;
artwork provenance is recorded from owner confirmation in THIRD_PARTY_NOTICES.md.
Upstream monitoring adaptations retain their notices. Codex approval detection stays
**Unsupported**; async question detection and correlated answer clearing are supported.
CI is configured locally but has no hosted run yet. Clean-machine and hands-on checks
remain tests to perform; the owner will handle hands-on behavior. Publication, signing
and channel decisions are deferred until after qualification. DSH Desktop 0.2.0-rc.2
and the Claude desktop app are installed; bridge activation and live session evidence
remain pending, with desktop/editor setup documented in the integration READMEs.

The material below is **historical, revision-specific planning and evidence**.
Older PASS rows certify only the source/archive named in their evidence, including
historical test totals. They do not certify the current remediation tree.
The clean-machine protocol remains the applicable unexecuted procedure.

---

# Historical repository diagnosis and release-candidate plan

Latest automated evidence (2026-09-27): private **RC8**, source `f14dc47`, includes the
async answer fix. `scripts/Test-PreUserRelease.ps1` now runs the six test areas in one
sequential pass. Evidence index: `artifacts/pre-user/run-20260928T065109Z-aab5b5d0/pre-user-report.json`.
Build clean; 722 .NET tests; 41 packaged checks passed, zero failed, three intentional
activation checks deferred; integration 17/17, composition 42/42, bridges 30/29,
upgrade/rollback 25/25 per mode, native scaling and silent voice passed. Seven further
report controls ensure missing/failed/unknown results cannot masquerade as success.
No new product defect was found by this pass; test reliability/cleanup defects were fixed.
**Manual/external qualification remains open** as recorded in each of the six report
areas, including live harness setup, clean-machine/download behavior, physical display
and lifecycle checks, audible voice and the unresolved Codex approval connection.
Older RC7/RC6 evidence below is historical; no live installation was replaced.

Current-source follow-up: the async answer-clearing defect below is fixed and verified
live during the active turn (Needs you -> Working on the actual answer); Release
suite **722/722 passed**. This source is newer than packaged RC7. The user confirmed
the earlier real approval dialog: that path is still not observable by the current
monitor and requires a correlated live event bridge. Full evidence and the connection
blocker are in the archived IMPLEMENTATION_HISTORY entry. Public attention support is
not fully qualified; question detection/clearing and approval detection are separate.

Live attention follow-up (2026-09-27, RC7 assemblies, installed Codex CLI
`0.158.0-alpha.2.1`): a real async multiple-choice request reached the monitor and
HUD view model as "Needs you". Its answer did **not** clear the flag during the
ongoing turn. A subsequent approval-test invocation yielded zero permission-request
records; the user subsequently confirmed manually approving its dialog. Evidence and
scope are in the archived IMPLEMENTATION_HISTORY entry and
`artifacts/live-attention-rc7/results.json`. No visible HUD or audio was qualified.
The historical R-09 statement below that *all* input events are unavailable is
incorrect and superseded: async input detection works; answer resolution and
approval detection need separate treatment before making release claims.

Latest Astra correction (2026-09-27): **PASS WITH NONBLOCKING NOTES** for the five
local review findings, fixed in `5aba6c2`. Current checks: 704 .NET tests, 29 Claude/
30 dsh bridge tests, 55 upgrade-comparison negative controls and 3 mocked placement
cases passed. Clean RC7 has been built and verified as follows; the older 28-row
matrix later in this document remains historical RC6 evidence, not an RC7 gate total.

| RC7 verification | Result | Evidence under `artifacts/rc7/` |
| --- | --- | --- |
| Clean package identity | `1.0.0-rc.7+5aba6c2`, unsigned, 568 files / 75 integration assets | `build-manifest.json`, `astra-packaged/identity.json` |
| Extracted real composition with invented sources | 42/42; seven window-placement assertions on DISPLAY2 | `astra-packaged/composition/run-20260928T045928Z-f563f2a5/composition-report.json` |
| Extracted monitoring-to-UI integration | 14/14 on DISPLAY2 | `astra-packaged/integration/run-20260928T050003Z-65249048/integration-report.json` |
| Silent voice mechanics | PASS, offline synthesis/preview and owned loopback | `astra-packaged/voice/stdout.log` |
| Real RC6 -> RC7 upgrade/rollback, HUD | 25/25; strict saved-window comparison | `upgrade-hud/run-20260928T045527Z-32c2c0fd/upgrade-evidence-20260928T045527Z.json` |
| Real RC6 -> RC7 upgrade/rollback, Window view | 25/25; strict saved-window comparison | `upgrade-window/run-20260928T045628Z-7f638b8b/upgrade-evidence-20260928T045628Z.json` |

RC7 archive SHA-256:
`88531C43F86071E6F7F4581C03FB30818404D874A484B668C50935FF5D93EC74`.
Full interactive RC7 lifecycle smoke was not repeated because second-launch restore
intentionally activates the window. Existing RC6 lifecycle evidence is unchanged.
Live-host, audible-audio, physical-display, clean-machine and distribution gates
remain open; public-release readiness is not claimed. See the newest
IMPLEMENTATION_HISTORY entry for full evidence and limits.


Historical execution packet: **Stage 1 cleanup + Stage 2 implementation**, below.
The original diagnosis/iteration plan remains as a historical baseline; current
verification and scope are in that packet and IMPLEMENTATION_STATE.md.

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

---

# Plan Handoff — Stage 1 cleanup + Stage 2 implementation

## Identity
Plan: Post-V1 broadened implementation, stage 2 of 2.
Goal: Close remaining stage-1 cleanup and implement the locally executable bulk of stage 2: distribution, update/rollback and compatibility qualification.
Branch: ux/first-use.
Base commit: 171f556.
Current head at planning: 171f556; the documentation-only handoff commit follows it.
Accepted product source: 389cb15074c287c9a241fd1c5eba045e8cbadad7.
Role: Astra planning; this packet is ready for DeepSeek execution when instructed.
Product: OVERSEER. Project: THE OVERSEER PROJECT.
This is not original V1 Plan 2 and does not alter the frozen four-plan specification.
One implementation goal spanning cleanup and stage-2 delivery, internal checkpoints,
then one Astra review. Do not stop after cleanup, a diagnostic pass or a test plan.

## End State
Remaining stage-1 defects and documentation gaps are closed, stage-2 code/scripts are
implemented, and a fresh self-contained win-x64 candidate has a qualification procedure,
with safe bridge installation/rebinding, tested update/rollback boundaries, accurate
first-run guidance, verified normal startup and explicit evidence per advertised
platform/harness. Complete all locally executable work; external gates may remain
blocked with exact prerequisites. Never equate implementation complete with public
release approved. No redesign or new feature family.

## Repository Facts
- Four projects: Core owns normalized models/state/history/voice policy; App owns
  composition, bridges and Windows I/O; UI owns WPF presentation; Tests owns xUnit.
  Keep C#/.NET 10/WPF/System.Text.Json/HttpClient/WAV, no additional framework.
- RC5: artifacts/rc5/OVERSEER-1.0.0-rc.5-win-x64.zip; source 389cb15;
  SHA-256 7D3B393F962D93FFFBCB1F7B276CD99AD144B9B25110A01452D5E17480C4E9A0.
  566 files, 73 integration assets, clean source manifest. Unsigned internal RC.
- Final stage-entry pass reran Release build/tests: 686/686; placement script 3/3.
  RC5 prior independently executed evidence: packaged integration 14/14, RC4->RC5
  upgrade 9/9. These are not a full RC5 lifecycle, physical or clean-machine pass.
  Bridge suites last verified 30/30 dsh and 21/21 Claude; rerun after changes.
- Corrected and protected: task-history rebuild recursion; old/stale attention
  accessibility; full-snapshot attention episodes; typed source health/freshness;
  startup focus guard lifetime; no-secondary upgrade deferral. Keep regressions.
- App.OnStartup -> HudComposition.CreateAsync/Prepare -> CodexMonitorAdapter +
  BridgeMonitorAdapter -> CompositeMonitorSource -> HudApplicationController.
  NotificationCoordinator records/evaluates before UI coalescing.
- HudIntegrationCheckRunner uses real readers/composite/controller but builds its
  own dependencies. Its 14 checks do NOT establish normal HudComposition wiring.
- HarnessSources discovery happens during composition; new bridge discovery requires
  OVERSEER restart. Do not add hot discovery in this stage.
- VoiceSettings.BaselineDefault selects offline speech, muted, for missing config.
  Existing preferences remain authoritative. Assigned recordings override selected
  provider; do not silently introduce cross-provider fallback.
- Path inventory in this planning session found codex.exe and node.exe on PATH;
  dsh and claude were not resolved there. Earlier dsh 0.1.5-rc.3 evidence is historical,
  not proof of current availability. Recheck actual installed host versions safely.
- Read-only dsh checkout: ../.tools/deepseek-harness-reference, verified commit
  46a7f68b0922371ce7144b668b90e377d8e799f4. Do not edit it.
- HANDOFF is ignored; this same packet is tracked in docs/RELEASE_READINESS.md.
  Prior handoff backup: artifacts/planning-stage2/HANDOFF-before-stage2.md.

## Scope and stage boundary
This is an implementation handoff, not a qualification-only assignment. The user
explicitly requests remaining stage-1 cleanup AND the bulk of stage-2 implementation
in one execution/review round. Treat stage 1 as a usable baseline, not as a claim that
every original presentation, normal-composition or physical-check criterion passed.

Stage-1 carryover to close first:
- Reconcile the original Iteration 1/UI contract in RELEASE_READINESS with the current
  implementation: single summary/scroller, optional history, stable project expansion,
  pending-input visibility, source-aware health, narrow layouts and header hit regions.
  Inspect current renders and regressions; fix reproducible defects in those existing
  behaviors. Do not replace the accepted design or reopen fixes already covered.
- Close the real HudComposition verification gap (checklist step 4) and stale README/
  controls/status guidance (step 6). These are carryover work, not optional polish.
- Review the resulting diff for stale bindings, duplicate state/handlers, unreachable
  controls and test-only paths masquerading as production evidence; remove only
  demonstrated dead/duplicate implementation. No speculative large refactor.

Stage 2 then delivers installer/rebind hardening, complete upgrade/rollback and
startup/shortcut checks, safe package/lifecycle tools, first-run compatibility tests,
the release evidence matrix and a new candidate. Tests must drive fixes; documenting
a known local defect does not satisfy this goal.
Missing external machines/hosts/audio sessions or owner distribution decisions may
block their particular qualification rows; they must not block the remaining code.

## Confirmed cleanup and stage-2 work, not already completed by green tests
1. Bridge DATA lives outside the app folder, but Claude install.mjs defaults its
   hook argument to bridge.mjs beside that installer. Removing an old extraction
   can invalidate that command. Test executable binding, not merely spool existence.
   --apply currently reports a missing bridge path yet can still write settings.
2. integrations/README.md incorrectly generalizes both integrations as having a
   preview installer; dsh uses its host plugin command and documents no dry-run flag.
3. Upgrade comparison covers a subset: not all roots/aliases/event overrides/clip
   assignments/window modes/close behavior. Placement keys are ignored; rollback
   and shortcut/startup rebinding remain untested. The spool assertion is existence,
   not preservation of populated producer data and executable references.
4. Package lifecycle currently force-terminates the owned tray process after restore;
   that is cleanup, not proof of packaged graceful Exit. It intentionally activates
   on second launch. Secondary placement alone does not make that nondisruptive.
5. Test-WindowScaling.ps1 starts unseeded WindowView and moves across every display.
   It must not run unchanged while the primary monitor is in use.
6. README begins with obsolete Codex-only/old-test summaries and old controls (e.g.
   cycling Recent hours), despite later newer sections. Consolidate the current guide.
7. No clean-OS/browser-download/independent-machine qualification or live dsh/Claude
   end-to-end acceptance has been demonstrated. No product distribution terms/signing
   decision has been made. No LICENSE or GitHub workflow is currently present.

## Relevant Code Map
### READ
- AGENTS.md; docs/V1_2_IMPLEMENTATION_SPEC.md; newest docs/IMPLEMENTATION_STATE.md;
  docs/RELEASE_READINESS.md; README.md; CHANGELOG.md; THIRD_PARTY_NOTICES.md.
- src/TheOverseer.App/App.xaml.cs, HudComposition.cs, HudApplicationController.cs,
  HudApplicationController.WindowView.cs, Desktop/StartupRegistration.cs,
  Desktop/StartupActivationGuard.cs, Desktop/InstanceScope.cs.
- src/TheOverseer.App/Diagnostics/{StartupArguments,HudPlacementOptions,
  HudIntegrationCheckRunner,HudFixtureRunner,VoiceSmokeRunner}.cs.
- src/TheOverseer.App/Monitoring/{CompositeMonitorSource,BridgeMonitorAdapter}.cs;
  src/TheOverseer.App/Monitoring/HarnessSources.cs and its discovery tests.
- src/TheOverseer.Core/Configuration/{MonitorConfig,MonitorConfigStore,HudSettings,
  HudWindowState,HudStateStore,VoiceSettings,ProjectSettings}.cs.
- src/TheOverseer.Core/Voice/NotificationCoordinator.cs;
  src/TheOverseer.App/Voice/VoiceProviderFactory.cs and
  Voice/Speech/{WindowsSpeechVoiceProvider,SapiSpeechSynthesisEngine}.cs.
- integrations/README.md and both integrations' README/NOTICE/tests; Claude
  install.mjs, uninstall.mjs, lib/settings.mjs; dsh package.json/cordis.patch.yml.
- scripts/Publish-Release.ps1, Test-PackagedRelease.ps1, Test-ReleaseUpgrade.ps1,
  Test-ReleaseUpgradePlacement.ps1, Test-WindowScaling.ps1.
- tests/TheOverseer.Tests: BaselineVoiceTests, VoiceSetupIntegrationTests,
  VoiceLifecycleTests, StartupRegistrationTests, StartupActivationGuardTests,
  SourceHealthTests, HudDispatchTests, HarnessBridgeTests, ProjectResolverTests,
  WindowPlacementTests, CompositeMonitorTests, HudViewModelTests.

- src/TheOverseer.UI/HudWindow.xaml and HudWindow.xaml.cs;
  ViewModels/{HudViewModel,HudViewModel.Simple,HudViewModel.TaskViews,
  HudViewModel.Activity,HudText,ProjectRowViewModel,TaskRowViewModel}.cs;
  tests/TheOverseer.Tests/{HudWindowTests,HudViewModelTests,SourceHealthTests}.cs.

### MODIFY
Above scripts, release/user/integration docs, targeted installer/helper tests.
Targeted UI/view-model cleanup for reproduced stage-1 contract defects is in scope.
Modify App/config/platform code for a reproduced carryover/qualification defect or a
small isolated diagnostic seam needed to exercise real composition/graceful exit.
Keep raw machine details and evidence in ignored artifacts, not user-facing copy.

### CREATE
Only useful existing-project regression tests and narrowly scoped qualification
scripts. Extend current release documents instead of creating per-check Markdown.
A local clean-machine checklist/evidence manifest belongs in RELEASE_READINESS.
No installer service, updater, new project, database or privileged helper.

### DO NOT TOUCH
Frozen AGENTS/spec/reference images; unrelated work; live HUD/data/shortcuts/startup
registry; real harness profiles, hooks or credentials; unrelated local applications.
No tool should alter these during automated fixture work.

## Architecture Invariants
- Monitoring failure is unknown/partial, never healthy zero. Peer publication cannot
  refresh another source; absent evidence remains unknown.
- Attention is evidence-backed; pending input, async needs-human and unsupported
  detection stay distinct. Preserve turn timers, episode identity and history bounds.
- Adapters observe only; never answer approval/question hooks, alter task execution
  policy or copy prompt/transcript/tool data into spools, logs or release evidence.
- Shared monitoring budgets, stable project/task/source identity, one notification
  coordinator, bounded I/O, voice dedupe/cooldown/mute and StartAsync vs Completion.
- Muted first run; user-triggered actions silent except explicit preview/opted startup.
- Same-scope restore is intentional activation; passive startup/updates cannot steal
  focus. Isolated scope never restores or exits the user's running instance.

## Upstream Reuse Map
- Reuse this repo's installer/settings planners, bridge normalizers and fixture suites.
  Extend ownership-aware rebind/removal, not unrelated host hooks or host internals.
- Read dsh pinned packages/core/session/src/{index,types,known-event-types}.ts for
  event vocabulary and packages/boot/plugin-manager/src/install-spec.ts plus its
  actual install implementation to determine local-path copy/link behavior. Adapt
  integration installation tests/docs; do not vendor the host. Human-question input
  was unobservable at this pin; do not fabricate an input signal.
- Claude primary contract: https://code.claude.com/docs/en/hooks
  Planning checked its command-hook fields: command + args is a documented direct
  executable form. Keep that form; do not mistake it for an invalid configuration.
  Verify exact target-version support and payloads on the actual host before claiming
  compatibility. Official reference supports schema expectations, not live evidence.
- Reuse StartupRegistration's IStartupRegistry seam for temporary target tests;
  a copied .lnk in a test directory can test shortcut paths without editing Desktop.
- Reuse current SAPI engine/provider and voice policy. No cloud account/model download.
- No old upstream HUD presentation, new adapter protocols or new provider integrations.

## Ordered Implementation Checklist
0. Complete the stage-1 carryover pass above and fix supported findings immediately.
   Use the original UI contract as the bounded checklist; attach a regression or
   render/runtime evidence appropriate to each fix. Record already-passing items
   without reimplementing them. Continue directly into all stage-2 work below.
1. Freeze baseline facts and establish one release acceptance matrix. Each entry has
   gate ID, candidate source/hash, environment/OS/build/scale/host version, command or
   steps, expected result, observed result, evidence path and status
   PASS/FAIL/BLOCKED/NOT RUN/UNSUPPORTED. Synthetic, live and physical evidence are
   distinct. Never turn missing prerequisites or skipped tests into PASS.
2. Finish reversible bridge installation/update/removal using isolated profiles:
   default to an explicit rebind workflow, using existing installers/host APIs;
   avoid inventing a full app installer. For Claude, refuse apply to missing payloads,
   preserve unrelated hooks, preview/apply/idempotent/rebind/remove, and invoke the
   actual generated command with an invented payload. Include spaces/non-ASCII paths.
   Rebind from old to new extraction, make old location unavailable, verify new hook
   runs and unrelated settings survive, then rollback/rebind/remove. For dsh verify
   host path semantics before writing instructions; if not testable, mark that gate
   blocked and retain experimental status. Stable spools alone are insufficient.
3. Strengthen candidate update/rollback checks on populated copies. Seed complete
   monitor roots/limits, harness roots/disables, project aliases/mappings, all relevant
   HUD settings and window modes/restore bounds, voice provider/mute/events/clip map/
   custom WAVs. Compare semantically; justify normalization instead of ignoring fields.
   Exercise RC5->new candidate->RC5. If backward writes lose data, provide and test a
   pre-update backup/restore rollback boundary, not an unqualified compatibility claim.
   Test copied shortcut and fake-registry startup targets across versioned directories;
   verify quoted executable + start-in-tray args and user opt-in preservation.
4. Cover normal composition, empty first run and restart. Drive startup configuration,
   discovery and provider selection through HudComposition (reuse/refactor a small
   dependency preparation seam if needed), not just manually assembled test adapters.
   Test no harness installed, missing/unsupported/unreadable source, valid mixed sources,
   bridge add/restart/remove/restart, stale/recovery. Require actual applied UI states.
   No automatic global harness installation and no hot discovery feature.
5. Close lifecycle/voice evidence gaps. Separate passive runs from tests intentionally
   activating windows. Add packaged graceful Exit evidence through the existing
   controller/UI path in a safe test session; forced kill remains timeout cleanup.
   Test startup guard zero prior foreground, explicit early show, normal timeout,
   user interaction and shutdown. Add display-selection/defer controls to scaling
   tests; do not iterate the user's primary screen implicitly. Prepare physical
   keyboard/Alt+Tab, drag/menu, Snap, 100/125/150/200% DPI, short/narrow/tall, max light,
   sleep/wake and monitor reconnect cases; do not change display settings on this
   machine to satisfy the matrix. Test baseline speech in isolated new/missing-voice
   profiles, real audible preview/notification when a test session is available,
   persistent immediate mute, no backlog replay, and silent user-driven actions.
6. Consolidate current README/UI/integration/setup/update/removal/rollback guidance.
   Put current project-first behavior first; label historical release evidence clearly.
   Correct spool-vs-hook path claims and dsh preview instructions. Keep optional
   integrations experimental until per-version live tests pass. Do not imply all
   Windows editions/harness versions are supported; record tested versions only.
7. Complete all available local tests, inspect relevant renders, commit source, then
   publish a NEW unused RC identity (rc.6 if free). Build from clean Git. Verify exact
   ZIP contents, no private payload, checksums/version/source, notices and actual
   Authenticode status. Re-run package checks after source/script changes that affect
   them. Any intentionally skipped interactive block remains an open gate.
8. Prepare an exact clean-machine protocol using that ZIP: browser download from an
   owner-approved channel when available, standard-user extraction/run without SDK,
   checkout or author's cached dependencies, empty-machine behavior, native prerequisites,
   optional host/Node setup, profile paths and update/rollback. Execute on an available
   disposable Windows environment and independent machine/account; record their actual
   OS builds. Reverify vendor support before declaring supported OS versions.
   No environment available: finish the runnable protocol and mark only those rows
   blocked. No public upload, account signup or purchase just to unblock testing.
9. Update ledger/changelog and report two separate outcomes: local implementation
   complete or incomplete; public qualification ready or blocked. Produce one
   review-ready branch with concrete owner/environment requests at the end.
   Do not abandon steps 1-7 because step 8 lacks a machine.

## Acceptance Criteria
- Stage-1 carryover audit is recorded and all reproduced local defects are fixed;
  cleanup alone is not completion. Stage-2 implementation must also be delivered.
- All available local regression/build/bridge/packaged tests pass for exact new source.
  No remaining ordinary code defect is called an external qualification blocker.
- Installer preview changes nothing; apply/rebind/remove preserves unrelated data;
  missing executable is rejected; generated hook runs after old path is unavailable.
- Full supported settings/recordings survive upgrade and the documented rollback
  route; shortcut/startup/bridge commands resolve to valid intended targets.
- Empty first run is usable, muted and honest. Offline voice availability is disclosed;
  existing preferences never silently reset. Normal composition is exercised.
- Stage-1 HUD/history/attention/focus regressions stay green; no new UI redesign.
- Every matrix row has evidence or an exact missing prerequisite. All advertised
  supported integrations have live per-version evidence; others remain experimental.
- Public readiness requires all release gates including clean download/external
  Windows, physical/audio, distribution/terms/signing decision; blocked is not PASS.
  Local implementation can finish with those explicitly external gates still open.

## Tests and Smoke Checks
Windows PowerShell from repository root, SDK on PATH:
    dotnet restore
    dotnet build TheOverseer.sln -c Release --no-restore
    dotnet test TheOverseer.sln -c Release --no-build
    pwsh -NoProfile -File scripts/Test-ReleaseUpgradePlacement.ps1
    node --test                 # run separately inside each integration directory
    git diff --check
Workspace SDK if needed: ../.tools/dotnet; set DOTNET_ROOT/PATH and task-local
DOTNET_CLI_HOME=../.tools/cli-home, NUGET_PACKAGES=../.tools/nuget-packages.

After committing source, using an unused candidate version:
    pwsh -NoProfile -File scripts/Publish-Release.ps1 -Version 1.0.0-rc.6 -ArchiveName OVERSEER-1.0.0-rc.6-win-x64.zip -OutputDirectory artifacts/rc6
    pwsh -NoProfile -File scripts/Test-ReleaseUpgrade.ps1 -PreviousArchivePath artifacts/rc5/OVERSEER-1.0.0-rc.5-win-x64.zip -CandidateArchivePath artifacts/rc6/OVERSEER-1.0.0-rc.6-win-x64.zip -EvidenceDirectory artifacts/rc6/upgrade
    pwsh -NoProfile -File scripts/Test-PackagedRelease.ps1 -ArchivePath artifacts/rc6/OVERSEER-1.0.0-rc.6-win-x64.zip -EvidenceDirectory artifacts/rc6/smoke
The last script CURRENTLY intentionally activates the HUD in lifecycle checks and
creates fixture popups; inspect/adapt its safe-run controls before invoking while
the user works. --placement-monitor does not itself prevent focus changes.
For passive isolated checks use extracted app --hud-integration-check with
--placement-monitor followed by the observed secondary device and a fresh output
directory. Do not assume that device is always DISPLAY2 on another machine.
Run full intentional-interaction checks only in an available test session that does
not disrupt the user. Report deferred checks explicitly; no false full-suite pass.
The existing scaling script needs modification per step 5 before use on this desktop.

## Risks / Uncertainties
- Clean VM/physical machine or live host missing: finish local work and state precisely
  which environment/version/event cannot be exercised. PATH absence is not proof the
  program is absent; locate known installs read-only before reporting availability.
- No owner distribution/license/signing choice: keep private unsigned internal RC;
  prepare factual options in release docs, do not invent redistribution rights.
- Host observability/version drift: retain unsupported capability and experimental
  label; only an architecture-changing upstream constraint warrants replanning.
- Windows/audio actions could disrupt work: isolate/defer, never fake observations.
- Credentials/real sessions are not test fixtures; use invented projects and approved
  host test profiles. Do not access credential stores or start paid model runs.
- Context capacity is not an environment blocker: commit coherent progress and record
  exact remaining checklist if a continuation is needed; do not call the packet done.

## Git Contract
Stay on ux/first-use; preserve unrelated changes. No force push, rewrite, merge, tag,
upload or deployment. Small logical commits; publish only clean source and record
package source separately from later doc-only HEAD. Never commit artifacts, local
paths/session content/credentials. Keep frozen sources byte-for-byte.
This handoff replaces historical fix prompts; no separate human micro-goals.
The user's current HUD, shortcuts, startup entry and real harness settings stay untouched.

## DEEPSEEK EXECUTION PROMPT
Act as DeepSeek implementation for OVERSEER: stage-1 cleanup plus the bulk of
stage-2 implementation for distribution and compatibility
qualification. Read AGENTS.md, this entire packet and the newest IMPLEMENTATION_STATE.
The accepted baseline is RC5 source 389cb15, planning HEAD 171f556 plus documentation;
686 tests pass, packaged integration 14/14 and upgrade 9/9. Execute the whole ordered
checklist, starting with stage-1 cleanup and continuing directly into stage 2. Do not
stop after cleanup or merely author test plans. Preserve the accepted HUD and monitoring
architecture. Prioritize real bridge executable rebinding, complete settings/rollback,
normal composition/first-run, safe lifecycle qualification and current user docs.
Do all available local work before reporting external gaps. Verify exact candidate
identity and tests; never confuse synthetic evidence with live, physical or clean-OS
qualification. Produce a new clean internal RC, honest acceptance matrix and one
review-ready branch. Stay off the user's primary work surface and do not seize input.
Do not replace their app, edit real host settings, purchase services, merge, push or
publish publicly. Local internal-candidate packaging is required. Return implementation status separately from public-release gate status,
with concrete evidence and the exact remaining environment/owner needs.

---

# Clean-machine qualification protocol

This protocol has **not been executed**. No clean Windows machine, VM or disposable
account was available in this round, so every row that depends on one is `BLOCKED`
with the exact prerequisite below rather than `PASS`. The procedure is written so it
can be run without the author's checkout, SDK or cached packages.

## Prerequisites the runner must supply

| Prerequisite | Why | If missing |
| --- | --- | --- |
| The candidate ZIP and its `.sha256` sidecar, obtained by **browser download** from an owner-approved channel | Proves the archive survives the real download path, including browser reputation handling | Upload is not authorized by this packet. Mark the download rows `BLOCKED (no approved channel)` and test from a copied file instead, recording that distinction. |
| A Windows machine or VM with no .NET SDK, no repository checkout, and no `%USERPROFILE%\.nuget\packages` cache | The self-contained claim is exactly "runs without a developer machine" | If the machine has any SDK, remove `DOTNET_ROOT` and any `dotnet` directory from `PATH` and record that it was not a pristine image. |
| A **standard user** account (not administrator) | Confirms no elevation is needed | Record the account type actually used. |
| The OS build, display scale and (for harness rows) the host version | Evidence is only meaningful with its environment | Record them from `winver`, the display settings page, and `dsh --version` / `claude --version`. |
| A disposable/independent machine for the second pass | Catches author-machine caching | If unavailable, the independent-machine rows stay `BLOCKED`. |

## Procedure

1. **Verify the download.** Record the browser, the channel URL and the downloaded
   file name. Compare SHA-256 against the published value:
   ```powershell
   Get-FileHash .\OVERSEER-1.0.0-rc.6-win-x64.zip -Algorithm SHA256
   ```
   Record the actual signature state; do not assume it matches the build machine:
   ```powershell
   Get-AuthenticodeSignature .\OVERSEER-1.0.0-rc.6-win-x64.zip | Select-Object Status, StatusMessage
   ```
   A browser reputation warning is an *observation to record*, not a test failure.
2. **Extract as a standard user** into a normal user-writable location. Include a path
   with a space in it at least once. Confirm `TheOverseer.App.exe` and `release.json`
   are present and that `release.json` records the expected version and source commit.
3. **First run, empty machine.** With no harness installed and no existing data
   directory, launch the executable. Expect: the HUD appears, is **muted**, reports
   unknown/limited health rather than a healthy zero, invents no tasks, and offers a
   setup action in *Projects & sources*. Record what the health control says.
4. **Native prerequisites.** Confirm the app starts with no .NET runtime installation
   and no SDK present. If it fails, record the exact missing component — a
   self-contained publish bundles .NET, not every OS/native dependency.
5. **Optional harness setup.** Only if a host is available, install a bridge using the
   integration README for that host, restart both host and OVERSEER, and confirm a
   synthetic or real session appears. Until this is done for a given host version,
   that integration stays **experimental**.
6. **Profile paths.** Record where the data directory and each spool actually landed
   (`%LOCALAPPDATA%\OVERSEER`, `...\bridges\<harness>\<instance>`,
   `...\spool\claude\<instance>`), and confirm they are outside the extracted folder.
7. **Update.** Extract a newer candidate into a *different* folder, run it against the
   same data directory, and confirm settings, recordings, project mappings, disabled
   sources, placement and spools survive.
8. **Rollback.** Follow `docs/UPGRADE_AND_ROLLBACK.md`. Record whether the documented
   boundary held on a real machine, not just in `scripts/Test-ReleaseUpgrade.ps1`.
9. **Remove.** Uninstall each installed bridge, confirm the host's unrelated settings
   are unchanged, delete the extracted folder, and confirm nothing in the user's
   profile references it any more.

## What this protocol cannot establish

Physical keyboard/Alt+Tab/Snap/drag behaviour, audible playback by ear, sleep/wake,
monitor removal/reconnect and mixed-DPI movement are **not** covered by the steps
above. They need a person at a real machine and remain separate rows.

---

## Historical release acceptance matrix (RC6-era evidence)

Recorded 2026-09-28 on branch `ux/first-use` (matrix recorded at HEAD `c69d51b`; R-10, R-15, R-17,
R-25 and R-26 were measured at HEAD `9f805c8` against the local `1.0.0-rc.6` archive). **`Status`
describes the run named in `Observed`**, never an untested candidate: `PASS` does not mean that a
`1.0.0-rc.6` archive was published — the archive, its `build-manifest.json` and its `.sha256`
sidecar now exist in `artifacts/rc6/`, but no owner-approved download channel exists and the
signing and distribution-terms decision is still outstanding. `Evidence class` is one of
`local-automated` (real scripts or the real apphost executed on this machine, possibly with invented
producer data), `synthetic` (fixture
archives, mocked launches, rendered fixture images), `live` (a real installed harness), `physical`
(needs a person, real hardware, display settings or a speaker) and `external-machine` (needs
another machine, another account or an owner decision). `Status` is one of `PASS` / `FAIL` /
`BLOCKED` / `NOT RUN` / `UNSUPPORTED`; a missing prerequisite or a skipped test is never recorded
as `PASS`.

| Gate ID | Gate | Evidence class | Command / steps | Expected | Observed | Evidence path | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| R-01 | Start: extract the self-contained win-x64 archive and run the apphost as a standard user | local-automated | `pwsh -NoProfile -File scripts/Test-PackagedRelease.ps1 -ArchivePath artifacts/rc4/OVERSEER-1.0.0-rc.4-win-x64.zip -EvidenceDirectory artifacts/rc4/smoke` | `extraction-apphost`, `extraction-self-contained`, `child-environment-sanitized` and `extraction-no-private-payload` pass with no workspace SDK path leaking into the child | rc.4 (source `c18822d`): 41/41 checks pass; `includedFrameworks` = .NET 10.0.12; `childEnvironmentLeaks` empty; extracted into a folder whose name contains a space and a non-ASCII character. Not rerun for rc.5 or rc.6 | `artifacts/rc4/smoke/run-20260927T232228Z-5d48094d/smoke-evidence-20260927T232228Z.json` | PASS |
| R-02 | Start: browser download of the published archive on a clean Windows install, standard protections intact | external-machine | Download the owner-approved archive in a browser, extract, run | Download completes, reputation/SmartScreen behaviour is observed and recorded, the app runs | BLOCKED: no owner-approved download channel and no clean Windows machine; the candidate binaries are `NotSigned` and no distribution/terms decision exists. Steps 1-2 of the clean-machine protocol above are not executed | not recorded in this pass | BLOCKED |
| R-03 | Start: first run with no config composes the real stack, stays muted and invents no work | synthetic | `TheOverseer.App.exe --hud-composition-check` (no `--data-directory`: the check now isolates itself under the evidence root) | `empty-first-run-composes`, `applies-a-snapshot`, `invents-no-work`, `starts-muted`, `is-not-a-healthy-zero` and `clean-shutdown` pass | 26 passed, 0 failed: `rows=0 working=? needsYou=? health=Monitoring offline muted=True details=[Codex:not readable · partial · root-missing] applied=1` (`empty-first-run` written under the run root, not the user's data directory). An earlier run in another directory recorded 21 passed / 5 failed and is superseded | `artifacts/composition-check-noarg/run-20260928T014636Z-a097864a/composition-report.json` | PASS |
| R-04 | Empty machine: no harness installed yields a usable HUD, honest health and no fabricated work | synthetic | Same composition check, no-harness cases | `no-harness-discovers-nothing`, `produces-no-tasks`, `health-is-honest` and `still-usable` pass | `sources=0 capped=False`; `rows=0`; `health=Monitoring offline`; `emptyState=present details=1` | `artifacts/composition-check-noarg/run-20260928T014636Z-a097864a/composition-report.json` | PASS |
| R-05 | Empty machine: a Windows installation with no .NET runtime, SDK, Node or harness | external-machine | Run the archive on a clean VM with no developer tooling (protocol steps 3-4) | The app starts without installing .NET, and the empty state plus setup guidance appear | BLOCKED: no clean or disposable Windows environment is available; the recorded extraction ran on this development machine with its SDK present (SDK paths were removed from the child environment only) | not recorded in this pass | BLOCKED |
| R-06 | Monitoring: known work and a failing source reach the applied HUD state through the production readers, composite and controller | synthetic | Extracted apphost `--hud-integration-check --output-directory <dir>` | Working, attention, answer, completion and degraded-source states are applied to the real view model | rc.5 (source `389cb15`): 14 passed, 0 failed, exit 0 — e.g. `codex-working-task-applied rows=3 working=3 health=Live`, `bridge-attention-applied needsYou=1`, `no-monitor-stopped-latch working=3+`. Not rerun at HEAD, where this runner was refactored into the HudComposition seam (`64ce198`) | `artifacts/rc5/astra-check/integration/run-20260928T011022Z-3f69ec33/integration-report.json` | PASS |
| R-07 | Monitoring: a failing source cannot erase a peer, no offline latch, recovery and advancing freshness | synthetic | Same integration run | `peer-source-survives-failure`, `no-monitor-stopped-latch`, `codex-recovers-after-clean-data`, `freshness-advances` and `released-file-stops-degrading-the-source` pass | All five pass in the rc.5 run: recovery on clean data, freshness applied 54 -> 73, and the released file stops degrading its source | `artifacts/rc5/astra-check/integration/run-20260928T011022Z-3f69ec33/integration-report.json` | PASS |
| R-08 | Attention: one blocking and one asynchronous request, the answer clears them, unknown stays disclosed | synthetic | Same integration run | `bridge-attention-applied`, `bridge-answer-clears-attention`, `async-attention-while-working` and `async-attention-survives-unrelated-output` pass | All four pass in the rc.5 run (`needsYou=1` while async attention is outstanding, 0 after the answer), with the limited-detection disclosure still shown separately | `artifacts/rc5/astra-check/integration/run-20260928T011022Z-3f69ec33/integration-report.json` | PASS |
| R-09 | Attention: real approval/input detection on the installed Codex version | live | Observe a live Codex approval or question in the running HUD | A pending request appears as needs-you with its own evidence | UNSUPPORTED for approval detection: no live correlated approval-event connection is configured. Async question detection and answer clearing were subsequently live-verified (d7bd131; IMPLEMENTATION_HISTORY.md). The owner elected explicit unsupported approval capability on 2026-09-29; no approval is inferred or answered | not recorded in this pass | UNSUPPORTED |
| R-10 | Harnesses: the shipped dsh and Claude bridge suites pass from an extracted archive | local-automated | `cd integrations/deepseek-harness; node --test` and `cd integrations/claude-code; node --test` | Both suites pass with 0 failures | dsh 30 tests, 30 pass, 0 fail, 0 skipped; Claude 25 tests, 25 pass, 0 fail, 0 skipped (the Claude suite grew from 21 with the installer-binding tests). Honesty note: an earlier concurrent run of the dsh suite recorded in `artifacts/rc6/verification/bridge-dsh.log` reported a failure in the concurrency test `concurrent writers produce whole, non-interleaved lines` while a packaged windowed suite was running heavy load on the same machine; that test is contention-sensitive and was observed to flake under load. The isolated run above is the recorded result, and the suite passes 30/30 without that contention, both before and after it | `artifacts/rc6/verification/bridge-dsh-isolated.log`, `artifacts/rc6/verification/bridge-claude.log` | PASS |
| R-11 | Harnesses: per-version live qualification of every advertised integration | live | Install a bridge into a real host profile (Claude Code CLI or a `dsh` profile), run a real session, then remove it (protocol step 9) | Records arrive, unrelated host settings survive install and removal, and the recorded hook still fires after the extraction directory moves | BLOCKED: no `claude` on PATH and no `~/.claude/settings.json` on this machine, and the dsh observer has never been attached to a live profile. The upgrade harness records "live bridge rebinding on a real host" in `checksNotPerformed`; both integrations stay experimental | `artifacts/upgrade-placement-tests/33c099b52c714915b2c4282cec15b0aa/secondary/run-20260928T014804Z-3e3e200f/upgrade-evidence-20260928T014804Z.json` | BLOCKED |
| R-12 | UI: the current HUD contract renders in both themes at the supported sizes | synthetic | `dotnet run --project src/TheOverseer.App -- --hud-fixture --output-directory artifacts/ui-cleanup-renders` | Renders for collapsed, expanded, one-project, collapsed-projects, activity-hour, history-open, filter-attention, health-details, light-top, light-right, recent, session, settings, empty and unavailable, plus the window-size matrix | 46 PNGs (24 `dark-*`, 22 `light-*`; the empty and unavailable scenarios are written as `dark-*` by the fixture runner) covering 300x220 through 1400x900, including the pinned header, the explicit Recent-hours selector, the All/Needs-you/Working filters and the disclosed history axis/legend. The rerun after the dead-code removal (`artifacts/ui-cleanup-renders2/`, also 46 files) is recorded in that commit as byte-identical | `artifacts/ui-cleanup-renders/light-expanded.png` (also `dark-recent.png`, `dark-history-open.png`, `dark-health-details.png`, `dark-window-300x220.png`) | PASS |
| R-13 | UI: native resize, maximize/restore, persisted bounds and the Snap hover target in an isolated run | local-automated | `pwsh -NoProfile -File scripts/Test-WindowScaling.ps1 -EvidenceDirectory artifacts/scaling-dev2-184414` (targets a named non-primary display) | The window opens on the requested display, resizes natively, keeps resize across updates, reports the maximize hit target, restores, and persists normal bounds | `deferred=false`, `targetDisplay=\\.\DISPLAY2`, `targetIsPrimary=false`, `primaryIncludedInTransitions=false`, `nativeResize=true`, `resizeSurvivesUpdates=true`, `nativeMaximizeHitTarget=true`, `maximizeRestore=true`, `normalBoundsPersisted=true`, `startedOnTargetDisplay=true`, `dpi=96`. `checksNotAutomated` still lists keyboard Snap, pointer drag and Windows scale changes | `artifacts/scaling-dev2-184414/window-scaling-evidence.json` | PASS |
| R-14 | UI: physical interaction and scale changes (Snap layouts by keyboard and hover, pointer drag, 125/150/200% scale, short/narrow/tall by hand, maximum light width) | physical | Exercise Snap, drag and Windows display-scale changes on the real desktop | Every interaction works at each scale without clipping or focus theft | BLOCKED: the recorded runs covered a single non-primary display at 96 DPI (`dpi: 96`) and no display setting was changed, because changing the user's scale is not authorized. `window-scaling-evidence.json` records Snap-by-keyboard, physical drag and scale changes under `checksNotAutomated`, and the packaged smoke lists human tray interaction and mixed-DPI movement in `physicalChecksNotPerformed` | `artifacts/scaling-dev2-184414/window-scaling-evidence.json` | BLOCKED |
| R-15 | UI: the audited UI contract defects and the dead-code removal have automated regressions that pass at HEAD | local-automated | `dotnet test TheOverseer.sln -c Release` (`tests/TheOverseer.Tests/UiContractCleanupTests.cs`) | Build 0 warnings / 0 errors and the whole suite passes | Release build 0 warnings / 0 errors; `dotnet test TheOverseer.sln -c Release` → 695 passed, 0 failed, 0 skipped, at source `9f805c85b3bc9ef8147923c214b0ec2f4b84643a` (recorded as `head=` in the verification context for the same run) | `artifacts/rc6/verification/build.log`, `artifacts/rc6/verification/dotnet-test.log`, `artifacts/rc6/verification/verification-context.txt` | PASS |
| R-16 | Lifecycle: same-scope restore, hide-to-tray, isolated scope and startup opt-in in a packaged run | local-automated | `Test-PackagedRelease.ps1` on the archive with the lifecycle block included | `hud-live-second-launch-restores`, `hud-live-close-hides-to-tray`, `hud-live-no-focus-steal`, `hud-tray-hidden-startup` and `hud-no-tray-graceful-close` pass | rc.4 (source `c18822d`): all pass inside the 41/41 run with `hudLifecycleSkipped=False` and `focusTakeovers` empty. This run predates the reworked lifecycle checks of `c98b13a`, so it is historical evidence for the current script | `artifacts/rc4/smoke/run-20260927T232228Z-5d48094d/smoke-evidence-20260927T232228Z.json` | PASS |
| R-17 | Lifecycle: packaged graceful Exit through the normal-composition path, with the placement flushed and no forced kill | local-automated | `pwsh -NoProfile -File scripts/Test-PackagedRelease.ps1 -ArchivePath artifacts/rc6/OVERSEER-1.0.0-rc.6-win-x64.zip -EvidenceDirectory artifacts/rc6/smoke -VisibleCheckMonitor \\.\DISPLAY2` (`hud-live-graceful-exit`, exit code 0 required) | The no-tray run closes through the controller, exits 0, and the state file is flushed without a forced kill | `hud-live-graceful-exit` passed with `windowSeen=True posted=True exited=True exitCode=0 forcedKill=False placementFlushed=True trayRunHideVerified=True`. The whole packaged smoke run was 44 checks, 0 failed, no skipped block, with every windowed check on the secondary display (`visibleChecksSkipped=False`, `visibleCheckMonitor=\\.\DISPLAY2`) and the focus check reporting `foreground samples=548 owned-foreground samples=0 takeovers=0` | `artifacts/rc6/smoke/run-20260928T032046Z-a64bf4b5/smoke-evidence-20260928T032046Z.json` | PASS |
| R-18 | Lifecycle: the three-launch upgrade contract (previous, candidate, rollback) with distinct data directories | local-automated | `pwsh -NoProfile -File scripts/Test-ReleaseUpgradePlacement.ps1` | Three launches; the rollback runs last with the previous package and never carries the candidate-only `--placement-monitor`; no run reuses another run's data directory in place; all three cases behave as contracted | The newest recorded run completed all three cases: `secondary` and `primary-opt-in` pass 25/25 checks with three mocked launches each, and `no-secondary` records deferred evidence with exit 1 and zero launches. Launches are mocked in this harness, so no window is opened | `artifacts/upgrade-placement-tests/33c099b52c714915b2c4282cec15b0aa/secondary/run-20260928T014804Z-3e3e200f/mock-launches.jsonl` | PASS |
| R-19 | Recovery: sleep/wake, monitor removal and reconnect, mixed-DPI placement restore, corrupt or locked files | physical | Suspend and resume, detach and reattach a monitor, change display scale, then inspect placement and health | Placement returns to the same physical spot, a removed monitor is clamped back onto a reachable one, and a corrupt file is preserved as `.corrupt-<stamp>` | BLOCKED: the recorded runs never suspended the machine or changed the display topology; the clean-machine protocol lists sleep/wake, monitor reconnect and mixed-DPI movement as rows it cannot establish, and the packaged smoke lists physical sleep/wake in `physicalChecksNotPerformed`. Corrupt-config fallback is covered by the automated suite only, with no run record in this pass | `artifacts/rc4/smoke/run-20260927T232228Z-5d48094d/smoke-evidence-20260927T232228Z.json` | BLOCKED |
| R-20 | Voice: a missing config selects the offline speech baseline, muted, and never resets existing preferences | synthetic | Same composition check (baseline-voice and existing-preferences cases) | `missing-config-selects-offline-speech`, `offline-speech-provider-is-constructed`, `provider-requires-no-network-endpoint` and every `existing-*` preservation check pass | `provider=speech muted=True`; `providerType=AssignedVoiceProvider`; `endpoint=none`; existing provider (`pack`), mute, theme, project mapping and disabled source all preserved, and startup does not rewrite the config | `artifacts/composition-check-noarg/run-20260928T014636Z-a097864a/composition-report.json` | PASS |
| R-21 | Voice: packaged pack validation and the owned loopback TTS smoke inside a release extraction | local-automated | `Test-PackagedRelease.ps1` on the archive (`voice-smoke`, `voice-smoke-owned-loopback`, `voice-pack-check`, `voice-pack-check-five-events`) | All four voice checks pass against the shipped pack and an owned loopback endpoint | rc.4 (source `c18822d`): all four pass inside the 41/41 run. Not rerun for rc.5 or rc.6 | `artifacts/rc4/smoke/run-20260927T232228Z-5d48094d/smoke-evidence-20260927T232228Z.json` | PASS |
| R-22 | Voice: real audio (audible notification and preview, missing device or voice, persistent mute, no backlog replay) | physical | Run `--voice-smoke --audible` in a test session and listen; repeat with the audio device or the installed voice unavailable | Clips are audible, a missing device or voice is reported specifically, mute persists, and nothing replays on unmute | BLOCKED: no audible session was run in this pass; `--voice-smoke` is silent by default, audible playback by ear is listed in `physicalChecksNotPerformed`, and the clean-machine protocol excludes it. The last audible evidence (`artifacts/p3-voice-audible`) belongs to Plan 3 at an older revision | not recorded in this pass | BLOCKED |
| R-23 | Upgrade: previous release to candidate preserves every seeded setting, recording and spool document | local-automated | `pwsh -NoProfile -File scripts/Test-ReleaseUpgradePlacement.ps1` (fixture archive, launches mocked) | Zero failing differences across the compared JSON paths, the recordings and the spool documents | 25 checks, 0 failed in both windowed cases: 89 compared JSON paths identical (0 differences), 5 pack files byte-identical, spool records 6 -> 6 with 0 parse errors, `current.json` identical across 40 fields, `health.json` identical, 10 non-log data files present, candidate identity differs from the previous | `artifacts/upgrade-placement-tests/33c099b52c714915b2c4282cec15b0aa/secondary/run-20260928T014804Z-3e3e200f/upgrade-evidence-20260928T014804Z.json` | PASS |
| R-24 | Upgrade: the recorded rollback boundary, the backup/restore route and the update targets | local-automated | Same harness; the primary-display case adds `-AllowVisibleChecksOnPrimary` | The rollback run succeeds and loses nothing, the restored backup reproduces the pre-update data, and the startup command plus the copied shortcut follow the candidate | `rollback.performed=true`, `previousBuildExitCode=0`, and all 89 paths plus recordings and spool records survived the backward run; `backupRestoreVerified=true` after the candidate copy was deliberately damaged (`config.json` and `bridges/` removed); the `StartupRegistration` probe quotes the candidate executable with `--start-in-tray`, rejects `dotnet.exe`, reports the stale recorded path after the move and preserves the opt-in; the copied `.lnk` follows the candidate directory. The real shortcut and `HKCU\…\Run` value are never read or written. The seed schema itself was probe-verified (`configStatus=Loaded`, 9 voice events, 5 clips, 3 roots/harness roots/disabled sources/mappings/aliases) | `artifacts/upgrade-placement-tests/33c099b52c714915b2c4282cec15b0aa/primary-opt-in/run-20260928T014809Z-e6c8b029/upgrade-evidence-20260928T014809Z.json` (seed: `artifacts/upgrade-seed-check/run-7e73b145/seed-probe.json`) | PASS |
| R-25 | Upgrade: the same comparison against the real published archives for this candidate (rc.5 to rc.6) | local-automated | `pwsh -NoProfile -File scripts/Test-ReleaseUpgrade.ps1 -PreviousArchivePath artifacts/rc5/OVERSEER-1.0.0-rc.5-win-x64.zip -CandidateArchivePath artifacts/rc6/OVERSEER-1.0.0-rc.6-win-x64.zip -EvidenceDirectory artifacts/rc6/upgrade` | Every seeded setting, recording and spool record survives, and the rollback run loses nothing | 25 checks, 0 failed. All 89 compared JSON paths identical after the candidate ran; the candidate → previous rollback run preserved the same 89 paths, the custom recording (5 pack files byte-identical) and the 6 spool records; the pre-update backup/restore boundary reproduced `config.json`, `state.json`, spool records and recordings exactly; the startup command was produced by the real `StartupRegistration` helper and quotes the candidate executable plus `--start-in-tray`; a copied `.lnk` target follows the candidate directory. Two real limitations are recorded rather than hidden: the integration-recorded absolute `bridge.mjs` path does not follow a versioned-directory move (`recordedPathFollowsPackageMove=false`), and a moved install reports `StalePath` until the user re-toggles "Start with Windows" | `artifacts/rc6/upgrade/run-20260928T032514Z-7fcf50d0/upgrade-evidence-20260928T032514Z.json` | PASS |
| R-26 | Distribution: a fresh candidate archive with a unique identity, payload audit, notices and recorded signing status | local-automated | `pwsh -NoProfile -File scripts/Publish-Release.ps1 -Version 1.0.0-rc.6 -ArchiveName OVERSEER-1.0.0-rc.6-win-x64.zip -OutputDirectory artifacts/rc6` | `build-manifest.json` with a unique release identity, a clean source tree, the payload list and the archive SHA-256; no private payload; integrations and notices present | `artifacts/rc6/OVERSEER-1.0.0-rc.6-win-x64.zip`, SHA-256 `2ECD384070FE21F8D4E694250B399C2664CEAFC3A276F5873B58BE05F9BA5931`, 76837418 bytes, 568 entries / 568 payload files including 75 integration assets, `sourceTreeDirty=false`, source `9f805c85b3bc9ef8147923c214b0ec2f4b84643a`, ProductVersion `1.0.0-rc.6+9f805c8`, apphost Authenticode status `NotSigned`, private-payload audit 0 offending files (`extraction-no-private-payload`: `files=568 offending=`) | `artifacts/rc6/build-manifest.json` | PASS |
| R-27 | Distribution: signing, redistribution terms and the download channel decision | external-machine | Owner decision: choose signing and distribution, confirm rights for the supplied assets, publish a channel | A signed or explicitly unsigned public artifact with stated terms and a real download path | DEFERRED by owner: original code/docs/artwork are MIT with upstream notices retained and artwork provenance recorded from owner confirmation. Signing/unsigned distribution choice and a public channel follow qualification. Historical candidate binaries are `NotSigned` | not recorded in this pass | DEFERRED |
| R-28 | Clean machine: the exact downloaded archive on a clean Windows VM and an independent physical machine or account | external-machine | Execute the clean-machine protocol above end to end, then repeat on an independent machine/account; record OS build, scale, harness version and hash | The app runs with no SDK, checkout or cached packages, setup guidance appears, and each machine's result is recorded | BLOCKED: no disposable Windows environment and no independent machine/account are available; the protocol is written but explicitly not executed, and `physicalChecksNotPerformed` records "clean machine without a global .NET runtime" | `docs/RELEASE_READINESS.md` (clean-machine qualification protocol, unexecuted) | BLOCKED |

**Status totals:** 28 gates — 19 `PASS`, 0 `FAIL`, 8 `BLOCKED`, 0 `NOT RUN`, 1 `UNSUPPORTED`. Every
`PASS` row names the revision and the evidence file it was measured from; the rc.4 rows come from
source `c18822d`, the rc.5 rows from source `389cb15`, and the composition, scaling, render and
harness rows were produced on the current working tree at HEAD `c69d51b`, except R-10, R-15, R-17,
R-25 and R-26, which were rerun at HEAD `9f805c8` against the local `1.0.0-rc.6` archive.

Rows R-01, R-06 to R-08, R-12, R-13, R-16, R-18, R-21 and R-23 to R-26 are **local automated**
evidence: real scripts, the real apphost and the real composition path executed on this machine,
with invented producer data, fixture archives or mocked launches where the row says so. They show
that the recorded code paths work; they do not show that a real host, a real display configuration
or a real download behaves the same way. R-13 in particular proves native resize, maximize/restore,
bound persistence and the Snap hover target on an isolated run, and says nothing about what those
gestures feel like to a person.

Rows R-02, R-05, R-14, R-19, R-22 and R-28 need an **external environment** (a clean or disposable
Windows machine, an independent account, a second display or scale, real suspend/resume, and a
speaker); R-11 needs a **live host** (the Claude Code CLI or the dsh observer attached to a real
profile); and R-27 needs an **owner decision** on signing, distribution terms and the download
channel. Those rows are `BLOCKED` with the exact missing prerequisite in `Observed`. R-09 is
`UNSUPPORTED` for approvals because no accessible correlated approval-event connection has been qualified. Async input questions and their answers were subsequently live-verified; see IMPLEMENTATION_HISTORY.md.
R-10, R-15, R-17, R-25 and R-26 are now `PASS` on recorded machine evidence at HEAD `9f805c8`:
the shipped bridge suites (dsh 30/30, Claude 25/25, run in isolation from an extracted archive),
the full .NET suite (695 passed, 0 failed, 0 skipped), the packaged graceful-exit smoke (44 checks,
0 failed, exit 0 with no forced kill), the real-archive rc.5 → rc.6 upgrade comparison (25 checks,
0 failed) and the fresh candidate archive identity. No gate row is left `NOT RUN`.

**Public-release readiness is not claimed.** The local `1.0.0-rc.6` archive exists but has not been
published to any owner-approved channel; the current rollback, startup-target and
integration-reference checks have been run against the real `rc.5 -> rc.6` archives only, not
against the older `rc.4 -> rc.5` pair; and no clean-machine, live-harness, physical-display,
audible-audio or signing/terms gate has passed. The local implementation evidence is strong for
first run, applied monitoring state, UI renders, native window behaviour, the bridge suites,
upgrade preservation and rollback; the remaining gates are missing prerequisites, not hidden
defects.


</details>
