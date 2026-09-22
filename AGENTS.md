# AGENTS.md — The Overseer

This repository is operated with a three-role coding-agent loop:

```text
Astra    → planning
DeepSeek → implementation
Astra    → review
```

The objective is maximum safe automation with minimum human relay.

The persistent product source of truth is:

```text
docs/V1_2_IMPLEMENTATION_SPEC.md
```

The compact persistent execution ledger is:

```text
docs/IMPLEMENTATION_STATE.md
```

The current plan handoff is:

```text
.agent/HANDOFF.md
```

---

## 1. Execution Units

V1 uses exactly:

```text
4 plans
4 implementation goals
4 Astra reviews
```

A plan and a goal are one automation unit.

Do not split a plan into separate human handoffs unless a genuine architecture blocker is found.

Branches:

```text
impl/p1-monitoring
impl/p2-hud
impl/p3-history-voice
impl/p4-release
```

---

## 2. Astra — Planning Role

Astra plans the whole current plan before DeepSeek starts.

Astra must inspect:

```text
docs/V1_2_IMPLEMENTATION_SPEC.md
docs/IMPLEMENTATION_STATE.md
repository tree
git status
recent commits
relevant source files
relevant tests
relevant upstream files when reuse is involved
```

Astra must then write:

```text
.agent/HANDOFF.md
```

The handoff is not a vague plan. It must provide the **sufficient statistics** DeepSeek needs to execute without re-planning the project.

### Required handoff structure

```markdown
# Plan Handoff

## Identity
Plan:
Goal:
Branch:
Base commit:
Current head:

## End State
One precise description of what must be true when the plan is complete.

## Repository Facts
Observed current projects, interfaces, important files, test state,
warnings, current data flow, and other facts DeepSeek would otherwise
have to rediscover.

## Relevant Code Map

### READ
Exact prerequisite files.

### MODIFY
Likely files to change.

### CREATE
Expected new files.

### DO NOT TOUCH
Stable/out-of-scope files.

## Architecture Invariants
Only the invariants relevant to this plan.

## Upstream Reuse Map
For each relevant upstream component:
- source path;
- reusable logic;
- adaptation needed;
- code not to copy;
- coupling risk.

## Ordered Implementation Checklist
Concrete internal checklist for this one large goal.

## Acceptance Criteria
Objective pass/fail criteria.

## Tests and Smoke Checks
Exact tests, commands, and runtime checks.

## Risks / Uncertainties
For each:
- risk;
- expected handling;
- stop condition.

## Git Contract
Branch, expected commit behavior, files not to commit, merge prohibition.

## DEEPSEEK EXECUTION PROMPT
A direct executable prompt containing the relevant context above.
```

Astra planning is incomplete until the final DeepSeek prompt can be run without a human rewriting or summarizing it.

Do not tell DeepSeek merely to “follow the spec”.

Do not dump irrelevant repository context.

Provide the smallest context that is sufficient for correct execution.

---

## 3. What “Sufficient Statistics” Means

DeepSeek should not need to rediscover:

- what the plan is trying to achieve;
- current branch/base/head;
- which components already exist;
- which files matter;
- what interfaces exist;
- what prior plan outputs can be trusted;
- what upstream code is reusable;
- what product invariants must remain true;
- what the acceptance tests are;
- what commands must pass;
- what is out of scope;
- what uncertainty would justify stopping.

Astra should inspect those facts once and hand them over compactly.

The goal is:

```text
Astra spends tokens understanding the repo.
DeepSeek spends tokens implementing.
Astra spends tokens reviewing the diff.
```

not:

```text
Astra understands repo
→ DeepSeek independently re-understands repo
→ reviewer independently reconstructs everything
```

---

## 4. DeepSeek — Action Role

DeepSeek begins every plan by reading:

```text
AGENTS.md
.agent/HANDOFF.md
docs/IMPLEMENTATION_STATE.md
```

Read the full V1 specification only when:
- the handoff references a specific section;
- an ambiguity remains;
- an architecture conflict is suspected.

DeepSeek executes the entire plan goal autonomously.

Expected loop:

```text
inspect referenced code
→ implement
→ test incrementally
→ run full build
→ run full tests
→ perform smoke checks
→ update IMPLEMENTATION_STATE.md
→ leave branch review-ready
```

DeepSeek may make multiple logical commits.

Do not require one human confirmation per checklist item.

### DeepSeek must preserve
- monitoring/state/UI/voice separation;
- bounded monitoring;
- privacy rules;
- Light/Dark shared layout;
- monitoring failure != zero tasks;
- voice dedupe/cooldown;
- V1 scope freeze.

### DeepSeek must not
- introduce speculative frameworks;
- add dependencies without necessity;
- import the old Codex Monitor HUD presentation layer;
- redesign later plans;
- modify unrelated user work;
- use destructive Git commands;
- hide failed tests;
- silently broaden scope.

### DeepSeek stop conditions

Stop with a concise `BLOCKED` report only if:
- required Codex observability truly does not exist;
- a core upstream assumption is false in an architecture-changing way;
- safe execution requires scope expansion;
- repository state makes safe continuation impossible.

Ordinary implementation problems are not stop conditions.

Solve them autonomously.

---

## 5. DeepSeek Completion Contract

Before handing the branch back:

```text
dotnet build
dotnet test
plan-specific smoke check
```

must be run.

Update:

```text
docs/IMPLEMENTATION_STATE.md
```

with:
- plan status;
- completed implementation;
- commits;
- build status;
- test status;
- smoke result;
- known limitations;
- blocker, if any;
- ready-for-review statement.

Do not merge the plan branch.

---

## 6. Astra — Review Role

Astra reviews the complete plan branch against `main`.

Astra reads:
- `AGENTS.md`;
- V1 spec;
- `IMPLEMENTATION_STATE.md`;
- `.agent/HANDOFF.md`;
- plan diff;
- tests/build output;
- relevant runtime result.

Review the implementation, not the intention.

Priorities:
1. monitoring/task-state correctness;
2. separation of monitoring/state/UI/history/voice;
3. failure behavior;
4. bounded local resource use;
5. privacy;
6. voice anti-spam correctness;
7. Windows lifecycle/focus behavior;
8. scope discipline;
9. risky-path tests;
10. upstream attribution.

Severity:

```text
BLOCKER
MAJOR
MINOR
NOTE
```

Final verdict:

```text
PASS
PASS WITH NONBLOCKING NOTES
CHANGES REQUIRED
```

If changes are required:
- Astra produces one compact ordered fix prompt;
- DeepSeek fixes on the same branch;
- build/tests/smoke are rerun;
- Astra performs a short re-review.

Do not create a new plan for review fixes.

---

## 7. Git Rules

`main` must remain reviewed/runnable.

No agent may:
- force-push;
- rewrite `main`;
- broadly `git clean -fdx`;
- reset unrelated work;
- delete unrelated branches;
- merge before Astra review passes.

DeepSeek may commit freely within the current plan branch using small logical commits.

The plan branch is the review unit.

---

## 8. State Documents

Keep documentation small.

Persistent:
```text
AGENTS.md
docs/V1_2_IMPLEMENTATION_SPEC.md
docs/IMPLEMENTATION_STATE.md
```

Ephemeral/current:
```text
.agent/HANDOFF.md
```

Do not create per-checklist Markdown files.

Do not turn the repo into an agent-documentation project.

---

## 9. Scope Rule

Before adding any V1 feature, ask:

```text
Does this materially improve:
1. knowing what agents are doing?
2. knowing whether an agent needs the user?
```

If no, defer it.

---

## 10. Plan Definitions

### Plan 1 — Monitoring
Goal: trustworthy normalized live Codex monitor.

### Plan 2 — HUD
Goal: usable persistent website-derived desktop HUD.

### Plan 3 — History + Voice
Goal: activity trace and useful non-annoying voice notifications.

### Plan 4 — Release
Goal: harden, package, smoke-test, and release V1.

The detailed acceptance criteria live in `docs/V1_2_IMPLEMENTATION_SPEC.md`.
