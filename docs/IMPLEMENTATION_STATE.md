# OVERSEER implementation state

## Current work

The attention/accounting fixes merged through PR #4 at `a9dd93b`. Main Verify and
the alpha.5 package workflow both passed on their first attempts at that revision.
The exact unsigned alpha.5 ZIP passed eight qualification groups on clean Windows,
including 42 composition and 17 production-path integration checks. Earlier failed
runs and retries remain in the local evidence; they are not erased by these passes.
The repository remains private while Winston reviews the publication packet.

The November 11 preparation leaves source changes uncommitted: download verification
instructions and a pinned public-only GitHub attestation step. Private builds skip
attestation explicitly. A local main-only privacy copy preserves the current source
tree and verifies the complete rewritten history; the existing remote is untouched.

- Claude publishes health directly when uncontended and uses fixed receipts under
  contention. The 250 ms write budget, 60 ms accounting slice and 64 slots remain.
- Abandoned intents and corrupt receipts preserve explicit loss evidence before
  cleanup. Completed loss episodes can recover without hiding their counters.
- Temporary scans and recoverable I/O retain limited coverage without inventing
  drops. Bounded nonce proofs distinguish completed intents from slot reuse;
  failed admission declines metadata publication and capacity recovers after exits.
  Cold intent lookup uses the parent acquisition allowance while reserving the
  unchanged 60 ms accounting slice; the marker reserve is not an admission cap.
- Pending attention confirmation belongs to each observed request. Loss withdraws
  confirmation, and an answer only settles its matching request.
- A Claude approval without identity can bind to one known blocker only when the
  call set is complete. Gaps, missing call IDs and overflow prevent that inference.
  DSH retains its explicit correlation rules.
- Question tools publish input events. Standalone permission notifications remain
  attention signals because some sandbox prompts have no PermissionRequest hook.
- A newly inferred permission for the sole known question shares its input episode
  and cue. Existing approvals, competing calls and incomplete call sets stay separate.
  Collapsed plan-mode markers invalidate call-set inference until a new boundary.
- Confirmed requests persist beyond the ten-second work-action lease. Source
  staleness, lost coverage and matching replies still withdraw or settle attention.
- The EOF lock fix reports reduced coverage even after the log is caught up.
  Unlock resumes from the same cursor. DeepSeek fixtures use synthetic directories.
- Snapshot caches use opened-file identity. Malformed sources and static links are
  isolated so healthy peers continue. Producer logs, reads and discovery are bounded.

Independent reviewers reproduced and verified fixes for approval and accounting
defects in the incoming handoff and reviewed the subsequent live-attention fixes.
Local Release build is clean. All 978 .NET tests pass, including 16 live-attention
regressions/controls. Five of the first 14 cases failed before the fix. The unchanged
producer suites passed 72 Claude and 45 DeepSeek tests and 16 producer timelines.
Two earlier local Claude timing failures are retained. Preserved-spool review
and the isolated full rerun passed. Both hosted Verify jobs and the branch package
workflow passed on `9ab2ed2`; no tests were weakened or skipped.

Main's later 10-landed-plus-one-drop failure has a deterministic matching scan
reproduction. New 1.0.7 cases cover scan/read recovery, exhausted cleanup, failed
admission, corrupt summaries, capacity recovery and nonce reuse. The original
failed CI spool was not retained; the local counterexamples and logs are preserved.

A delayed permission notification without sufficient correlation can conservatively
retain attention until an explicit settling boundary. Failed incomplete-marker
cleanup can overcount a loss episode. Neither limit silently drops loss evidence.

## Qualified baseline

Main CI archive `1.0.0-alpha.5` was built from `a9dd93b` by run `42981235766`.
SHA-256: `202B8887D2451EF29658CF4A6E81826CEF6637020620E5470F3B89FB14FF702E`.
Clean standard-user Windows 11 build 26100 passed eight groups without developer
prerequisites. The audio pipeline checks were silent; Winston separately heard
one actual Codex-to-Kokoro input cue from this exact alpha.5 HUD on November 11.

Private branch archive `1.0.0-alpha.3` was built from `9ab2ed2` by CI run
`42964842458`. SHA-256:
`5BEDEAA2C1F5A812485C0C667793315EC8322981B72B49BA267165B4BC1DA6CA`.
That exact unsigned ZIP passed seven qualification groups on fresh standard-user
Windows 11 build 26100 without developer prerequisites, including nine Kokoro
clips and 42 composition checks. It predates the current live-attention fixes.

`98ec4a5` passed hosted Verify with 944 .NET, 54 Claude and 45 DeepSeek tests,
16 fixed producer timelines, 85 site checks and 13 publication controls.
The private CI archive `1.0.0-alpha.2` is unsigned and self-contained.
Its SHA-256 is `C63D293750BDD2552AB2CB64CED857CBDD63DA13ACCBF4D35CDE14D4A0FE78EB`.

That exact ZIP passed on fresh Windows 11 IoT Enterprise LTSC Evaluation x64,
build 26100, as a standard user without a .NET SDK/runtime installation, Node,
Git or NuGet cache. Defender was enabled. Twelve lifecycle checks, all nine
Kokoro clips, the silent voice pipeline and 42 composition checks passed.
Separate developer-machine upgrade and rollback passed 25 checks.

A local Claude concurrency run failed once during VM setup, then passed alone
and in both hosted jobs. Initial environment/setup failures remain in the
evidence. These results apply to alpha.2, before the Claude 1.0.6 changes.

## Product contracts

- Codex Windows sessions, including user-facing agent-created/handoff chats, are
  discovered locally. Genuine subagents and reviews remain excluded.
- Codex approvals are Unsupported. Async questions and correlated replies are
  observed. A dismissed card has no cancellation signal. After 15 minutes, older
  questions become unconfirmed while retaining identity for late replies.
- Claude Code and DeepSeek Harness integrations are experimental and read-only.
  The exact alpha.5 HUD passed real Claude question/answer and ExitPlanMode
  approval/resume checks on November 11 with Haiku. Both waiting rows cleared on
  response; other permission kinds and DSH Desktop attention remain unverified.
- The Claude Projects observer covers exposed loaded desktop overviews. It is
  bounded and nonactivating. Dedicated worker titles have ID fallbacks. Closed
  views and account-wide worker status remain unknown.
- New profiles start muted with the included offline Kokoro cues. Saved provider
  choices and voice deduplication/cooldowns remain authoritative.
- One actual Codex-to-Kokoro input cue was heard by Winston. Preview, silent
  provider checks and human listening establish different parts of the path.
- Focus pins use bounded JSON preview and confirmation and do not change task counts.
- Both themes share layout. Header dragging and status-light resizing follow pointer
  direction. Normal monitoring updates do not activate the HUD.

## Publication decisions

Original code, docs and owner-confirmed artwork use MIT. Upstream monitoring
adaptations and redistributed runtime files retain their notices.
Winston selected an unsigned public alpha. No code-signing identity is required for
this candidate. Download instructions disclose the Windows warning and checksum check.
Public release, repository visibility, website deployment and announcements need
Winston's approval.

The [privacy preparation](PRIVATE_GITHUB_STAGING.md) covers the isolated sanitized
history and original-to-public mapping, including removal of personal website
reference images. Older private remote history still contains
personal addresses and paths; do not expose that repository by changing visibility.

## Remaining qualification

- The merged-main alpha.5 CI ZIP and clean-Windows qualification are complete.
  New source/workflow edits need their own review and hosted checks before use.
- Keep the alpha.5 extraction used by the 15 rebound Claude hooks at a stable path.
  The installer verified 1.0.7 and its follow-up preview has zero changes.
- Actual alpha.5 HUD Claude question and plan-approval checks are complete.
  DSH Desktop input and approval remain separate live-host checks.
- Browser acquisition/SmartScreen and an independent machine remain open.
- Physical mixed-DPI movement, monitor reconnect, sleep/wake, logon and device
  audio need an available owner session. Record deferrals and actual observations.
- Resolve history/email exposure and verify public URLs and download hashes after
  authorized publication.

[Release readiness](RELEASE_READINESS.md) retains protocols and older revision
evidence. [Implementation history](IMPLEMENTATION_HISTORY.md) holds the archived
ledger. Local execution evidence and the next commands are in ignored
`.agent/HANDOFF.md`.
