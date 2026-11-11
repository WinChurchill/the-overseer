# OVERSEER implementation state

## Current work

Branch `fix/detection-logic-cleanup` is based on `98ec4a5`.
The repository remains private. The Claude 1.0.6 source passed independent review.
The release workflow must build the reviewed merged revision before hook rebinding.

- Claude publishes health directly when uncontended and uses fixed receipts under
  contention. The 250 ms write budget, 60 ms accounting slice and 64 slots remain.
- Abandoned intents and corrupt receipts preserve explicit loss evidence before
  cleanup. Completed loss episodes can recover without hiding their counters.
- Pending attention confirmation belongs to each observed request. Loss withdraws
  confirmation, and an answer only settles its matching request.
- A Claude approval without identity can bind to one known blocker only when the
  call set is complete. Gaps, missing call IDs and overflow prevent that inference.
  DSH retains its explicit correlation rules.
- Question tools publish input events. Standalone permission notifications remain
  attention signals because some sandbox prompts have no PermissionRequest hook.
- The EOF lock fix reports reduced coverage even after the log is caught up.
  Unlock resumes from the same cursor. DeepSeek fixtures use synthetic directories.
- Snapshot caches use opened-file identity. Malformed sources and static links are
  isolated so healthy peers continue. Producer logs, reads and discovery are bounded.

Independent reviewers reproduced and verified fixes for approval and accounting
defects in the incoming handoff. Local Release build is clean. All 959 .NET,
60 Claude and 45 DeepSeek tests and 16 producer timelines pass.
Two earlier local Claude timing failures are retained. Preserved-spool review
and the isolated full rerun passed. Hosted CI remains the merge gate.

## Qualified baseline

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
  Real lifecycle events were observed. The current hook version needs a fresh
  live question and permission/reply qualification.
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
No code-signing certificate is available. Private candidates are explicitly unsigned.
Public release, repository visibility, website deployment and announcements need
Winston's approval.

Winston requested an email privacy plan before public exposure. The plan is in
[private GitHub staging](PRIVATE_GITHUB_STAGING.md). Older history contains personal
addresses and workstation paths even though current packaged fixtures are scrubbed.

## Remaining qualification

- Review the final source, merge after hosted Verify, and build a new CI archive.
  Package evidence must identify that exact source revision and ZIP hash.
- Keep the extraction used by installed Claude hooks at a stable path. Rebind using
  the packaged installer, preserving unrelated settings and its backup.
- Qualify Claude 1.0.6 question/input response and permission/resume on the actual
  host. DSH Desktop input and approval remain separate live-host checks.
- Repeat first launch and composition on the final package in clean Windows.
  Browser acquisition/SmartScreen and an independent machine remain open.
- Physical mixed-DPI movement, monitor reconnect, sleep/wake, logon and device
  audio need an available owner session. Record deferrals and actual observations.
- Resolve history/email exposure and verify public URLs and download hashes after
  authorized publication.

[Release readiness](RELEASE_READINESS.md) retains protocols and older revision
evidence. [Implementation history](IMPLEMENTATION_HISTORY.md) holds the archived
ledger. Local execution evidence and the next commands are in ignored
`.agent/HANDOFF.md`.
