# Coding-agent instructions

## Start here

Read this file, `docs/IMPLEMENTATION_STATE.md`, Git status/diffs, and the code/tests
relevant to the request before editing. Follow the current user instructions when
they override this document. The [README](README.md) covers source setup;
[CONTRIBUTING.md](CONTRIBUTING.md) contains build and validation commands.

| Document | Purpose |
| --- | --- |
| [V1 specification](docs/V1_2_IMPLEMENTATION_SPEC.md) | Product scope and architecture. Read the relevant sections when behavior or boundaries are unclear. |
| [Implementation state](docs/IMPLEMENTATION_STATE.md) | Compact current ledger: status, evidence and unresolved limits. Keep within 200 lines. |
| `.agent/HANDOFF.md` | Local, ignored execution packet. It may not exist in a fresh clone. Create/update it for significant work; do not require a tester to obtain it. |
| [Tester handoff](docs/TESTER_HANDOFF.md) | Tracked setup, acceptance checklist and reporting instructions for another person. |
| [Implementation history](docs/IMPLEMENTATION_HISTORY.md) | Completed work and historical plans; not the current task list. |

## Plan → implement → review

The established roles are Astra for planning/review and DeepSeek for implementation.
Use the user's selected agent/workflow; these responsibilities also apply when one
agent performs the work. Same-chat review is not independent review.

1. **Plan:** inspect the current implementation and define a concrete end state,
   scope, acceptance checks and risks. Keep planning proportional to the change.
2. **Implement:** complete the agreed work, test meaningful failure paths and
   preserve unrelated work. Solve routine problems without per-step confirmations.
3. **Review:** examine the complete diff and executed evidence, correct findings on
   the same branch, rerun affected checks, and leave reviewable changes.

For a significant handoff, record enough for the next agent to continue without
reconstructing the project:

- Goal, branch, base/current commit, working-tree changes and intended end state.
- Relevant facts and paths to read/change; interfaces, invariants and out-of-scope files.
- Ordered work, objective acceptance criteria and exact validation commands.
- Actual results, evidence locations, remaining risks and the next executable step.
- Git contract and any action that requires the user's decision.

Include upstream paths, adaptation and attribution when reusing source. Update the
packet and compact ledger before handing back; do not create per-checklist docs or
paste obsolete release diaries into current instructions.

## Architecture and product invariants

- Keep monitoring, normalized state, UI, history and voice separate. Core owns
  evidence/state/policy; UI owns view models/views; App composes Windows services.
- Bound file reads, discovery, caches, history, logs, spool sizes and resource use.
  One broken source must not disable other sources.
- Monitoring failure is **not zero tasks**. Preserve unknown, partial, stale and
  unavailable coverage; never infer a response/approval from arbitrary text.
- Codex approval detection is **Unsupported**. Do not turn a synthetic fixture
  into a supported capability or claim real-host compatibility from contract tests.
- Keep harness interaction read-only. Preserve metadata allowlists and privacy;
  never put raw sessions, prompts, credentials or personal paths in committed evidence.
- Preserve voice deduplication/cooldowns, muted first run and explicit provider choice.
- Light and dark themes share layout. Preserve nonactivating status updates,
  single-instance/tray behavior and explicit lifecycle failure handling.
- Do not import the upstream Codex Monitor HUD presentation layer. Preserve all
  upstream notices and document adaptations.
- Keep V1 scope focused on knowing what agents are doing and when they need the
  user. Discuss architecture/scope changes; avoid speculative frameworks and dependencies.

## Validation and review

Run the Release build, .NET tests, relevant integration tests and a change-specific
smoke check using [the contributor commands](CONTRIBUTING.md). Use invented records,
disposable profiles and owned output directories. Never replace a live installation
or edit personal host profiles merely to validate a change.

Report passes, failures, unrun checks and environment restrictions separately.
Preserve failed evidence; do not weaken tests or silently change expected behavior.
A local testhost abort is not a test pass. Existing CI evidence applies only to its
recorded source revision. Package tests apply to the exact recorded ZIP hash.

Review priorities: state correctness; layer boundaries; failure isolation and bounded
resources; privacy; voice anti-spam; Windows focus/lifecycle; scope; meaningful tests;
attribution. Use `BLOCKER`, `MAJOR`, `MINOR`, `NOTE` for findings and conclude with
`PASS`, `PASS WITH NONBLOCKING NOTES`, or `CHANGES REQUIRED`. Give one ordered fix
list if changes are required.

## Git and completion

- Winston handles commits and pushes. **Do not commit or push. Never force-push.**
- Inspect status and diffs first. Preserve existing work; do not reset, discard or
  stash changes just to get a clean tree. Ask only if overlapping work is ambiguous.
- Use a suitable task branch or a descriptive branch from reviewed `main` for
  significant changes. Never create `claude/` branches.
- Do not rewrite history, broadly clean the checkout, or delete unrelated branches.
  Keep `main` reviewed and runnable; a passing review alone is not a request to merge.
- Leave changes uncommitted, with a concise report of behavior, validation and limits.
  Do not commit generated artifacts/logs, private data or `.agent/HANDOFF.md`.
- Publication, signing, live-host setup and physical/clean-machine qualification
  are separate from source review. Follow [release readiness](docs/RELEASE_READINESS.md).

Continue through ordinary implementation problems. Stop dependent work and explain
the concrete decision if required observability is absent, an upstream assumption
fails in an architecture-changing way, safe work needs scope expansion, or repository
state prevents safe continuation. Continue independent work where possible.
