Project name: The Overseer.
Use “The Overseer” as the product/application name in repository metadata, namespaces where appropriate, window/title text, packaging, docs, and user-facing strings. Do not otherwise reinterpret or expand the frozen specification.

# Agent HUD — V1.2 Implementation Specification

**Status:** implementation source of truth  
**Target:** private Windows application, usable by the end of the implementation session  
**Stack:** C# / .NET 10 LTS / WPF  
**Execution model:** Astra plans → DeepSeek implements → Astra reviews  
**Automation model:** 4 plans / 4 goals / 4 reviews  
**Visual reference:** supplied personal-website light and dark screenshots  
**Supersedes:** V1.1 execution structure

---

# 1. Product Definition

Agent HUD is a small always-on-top Windows companion for Codex sessions.

It should answer four questions without opening Codex:

1. How many agent tasks are working?
2. Does anything need me?
3. What is each task doing?
4. Was useful activity happening while I was away?

The product is not a dashboard, terminal replacement, analytics suite, pet framework, or chat-history viewer. It should feel like a small operator instrument at the edge of the desktop.

Core interaction:

```text
small persistent HUD
    ↓ click
expanded current-task list
    ↓ click task
open/focus matching Codex task where supported
```

Attention path:

```text
Codex event/state transition
    ↓
normalized attention event
    ↓
dedupe / cooldown / aggregation
    ↓
voice pack OR generic TTS endpoint
    ↓
human hears only meaningful events
```

---

# 2. End-of-Session Definition of “Usable”

The implementation session is successful if a packaged Windows build can:

- launch without a development environment;
- discover real local Codex tasks;
- show a correct working-task count;
- distinguish `WORKING`, `NEEDS_APPROVAL`, `DONE`, and monitoring failure;
- expand into a concise task list;
- show a useful current-action string;
- draw a 60-minute activity sparkline;
- play at least one voice-pack event;
- support a generic TTS endpoint;
- mute/unmute;
- persist its position;
- switch between Light and Dark website-derived themes;
- expose monitoring failure instead of silently showing `0 working`.

Perfect final visual polish is not required. A coherent, reliable instrument is.

---

# 3. Scope Freeze

## Required

### Monitoring
- Discover active Codex sessions.
- Support Desktop, CLI, and VS Code where observable.
- Deduplicate multiple rollout files belonging to one conversation.
- Reconstruct normalized task state.
- Derive one concise current-action label.
- Detect human-attention states from explicit events whenever possible.
- Distinguish task error from monitoring error.
- Maintain short rolling activity history.
- Recover from sleep/wake and temporary file locks.

### HUD
- Borderless.
- Semi-transparent.
- Draggable.
- Configurable always-on-top.
- Collapsed and expanded modes.
- One shared geometry with Light/Dark resources.
- Summary: `N working · N needs you`.
- One chart: recent active-agent count.
- Expanded task list.
- Current action per task.
- Monitoring-health indicator.
- Task click opens/focuses Codex where supported.
- Position persistence.

### Voice
- Static voice packs.
- One generic TTS endpoint.
- No mandatory local generation runtime.
- Event dedupe.
- Cooldown.
- Completion aggregation.
- Instant mute.

### System
- Tray icon.
- Start with Windows.
- Local config/state/logs.
- Multi-monitor recovery.
- DPI awareness.
- Clean shutdown.

## Explicitly out of V1
- arbitrary UI plugins;
- arbitrary third-party XAML;
- theme marketplace;
- Live2D/pet framework;
- built-in GPT-SoVITS/Fish/Piper runtimes;
- token/cost dashboards;
- historical reports;
- cloud sync;
- user accounts;
- MCP server;
- split task bubbles;
- multiple dashboard layouts;
- complex animation editor;
- macOS/Linux UI;
- full task-control surface.

---

# 4. Upstream Reuse Strategy

Do **not** fork Codex Monitor HUD as the product.

Do **not** clean-room all Codex monitoring.

Use:

```text
https://github.com/LH-03/codex-monitor-hud
```

only as the source for difficult monitoring plumbing.

Candidate components to adapt:

```text
CodexMonitorHud.Core/
  Sessions/
    SessionDiscovery
    SessionChangeTracker
    IncrementalJsonlReader
    BoundedTailReader
    SessionIdentityReader
    SessionTitleIndex
    SessionProfile

  Parsing/
    HudRecordParser

  State/
    SessionMonitorEngine
    TaskNumberPool

  Models/
    only records required by monitoring
```

Also evaluate:

```text
CodexMonitorHud.App/WindowsSessionActivitySource.cs
```

Do not import:
- `MainHudView`;
- old multi-layout presentation logic;
- split bubbles;
- pricing UI;
- PowerShell settings host;
- old theme/layout matrix;
- old animation machinery;
- MCP display features.

Create `THIRD_PARTY_NOTICES.md` and preserve upstream licensing/provenance.

---

# 5. Frozen Tech Stack

```text
Language:        C#
Runtime:         .NET 10 LTS
Desktop UI:      WPF
Primary target:  win-x64
Tests:           xUnit
Config:          JSON / System.Text.Json
HTTP:            HttpClient
Audio:           WAV playback
Packaging:       dotnet publish
```

Use WPF primitives:
- `Border`
- `Grid`
- `TextBlock`
- `ItemsControl`
- `Polyline` or tiny custom sparkline control
- `ResourceDictionary`

Do not introduce:
- React;
- Electron;
- WebView2;
- Tauri;
- Avalonia;
- chart frameworks;
- a database.

Runtime data:

```text
%LOCALAPPDATA%\AgentHud\
  config.json
  state.json
  logs\
    agenthud.log
  voicepacks\
```

First release:

```text
AgentHud-v1-win-x64.zip
```

No installer is required for V1.

---

# 6. Repository Structure

```text
agent-hud/
├── AGENTS.md
├── AgentHud.sln
├── README.md
├── THIRD_PARTY_NOTICES.md
├── .gitignore
├── .editorconfig
├── Directory.Build.props
│
├── .agent/
│   └── HANDOFF.md                 # generated by Astra; overwritten per plan
│
├── docs/
│   ├── V1_2_IMPLEMENTATION_SPEC.md
│   ├── IMPLEMENTATION_STATE.md
│   └── UI_SPEC_v0.2.md            # optional later refinement
│
├── design/
│   └── reference/
│       ├── website-light.png
│       └── website-dark.png
│
├── src/
│   ├── AgentHud.App/
│   ├── AgentHud.Core/
│   │   ├── Models/
│   │   ├── Monitoring/
│   │   ├── History/
│   │   └── Voice/
│   └── AgentHud.UI/
│
└── tests/
    └── AgentHud.Tests/
```

`AGENTS.md` is persistent and versioned.

`.agent/HANDOFF.md` is an execution artifact. It is generated by Astra at the beginning of each plan and overwritten for the next plan. It may be gitignored.

`docs/IMPLEMENTATION_STATE.md` is the persistent compact state ledger.

Do not create one Markdown file per subtask.

---

# 7. Core Component Responsibilities

## `CodexMonitor`
Owns:
- session discovery;
- file watching;
- incremental record consumption;
- conversation/session identity;
- liveness enrichment;
- monitoring-health signals.

Does not own UI or voice.

## `AgentStateReducer`
Owns canonical task state and transitions.

Input:

```text
RawCodexEvent
RuntimeActivity
PermissionEvent
InputRequestEvent
MonitoringEvent
```

Output:

```text
AgentTask snapshot
AgentTask transition
```

## `CurrentActionClassifier`

Canonical actions:

```text
Thinking
Reading files
Editing files
Running command
Running tests
Searching
Waiting for approval
Waiting for input
Completed
Idle
Error
```

Priority:
1. structured event;
2. command heuristic;
3. turn-state fallback.

## `AttentionDetector`

Events:

```text
PERMISSION_REQUIRED
INPUT_REQUIRED
TASK_ERROR
TASK_COMPLETED
ALL_TASKS_COMPLETED
```

Evidence priority:
1. explicit hook/protocol event;
2. explicit session record;
3. strong structured inference;
4. timing heuristic.

Never infer `NEEDS_INPUT` from mere inactivity.

## `MonitoringHealth`

```text
HEALTHY
DEGRADED
UNAVAILABLE
```

Prevents:

```text
monitor broken == 0 tasks
```

## `ActivityHistory`

Stores:

```text
timestamp
working_count
needs_human_count
```

Window: 60 minutes.

## `VoiceManager`

```text
AttentionEvent
  → VoiceEventPolicy
  → dedupe
  → cooldown
  → aggregation
  → VoicePackProvider or TtsEndpointProvider
  → playback
```

## `HudViewModel`

```text
MonitoringStatus
WorkingCount
NeedsHumanCount
ActivitySeries
Tasks[]
IsExpanded
IsMuted
Theme
```

The UI never parses raw Codex data.

---

# 8. Canonical Task States

```text
WORKING
NEEDS_INPUT
NEEDS_APPROVAL
ERROR
DONE
IDLE
```

Important: upstream `active/listening/idle` values are evidence only. They are not exposed directly as the product state.

---

# 9. UI Direction From the Supplied Website

Shared grammar:
- rectangular technical structure;
- thin borders;
- strong negative space;
- restrained warm accent;
- heavy sans-serif hierarchy;
- compact uppercase/monospaced metadata;
- subtle grid/line motifs;
- occasional clipped/angled corner;
- warm cream rather than pure white;
- green-black/charcoal rather than pure black;
- minimal animation.

System fonts:

```text
Primary:   Segoe UI Variable
Metadata:  Cascadia Mono
Fallback:  Consolas
```

Initial Light:

```text
BackgroundBase    #EEECE2
PanelSurface      #F5F3EA
Foreground        #202521
Muted             #666B65
Border            #C8C8BA
AccentPrimary     #AC4A1C
AccentSecondary   #C8B89B
```

Initial Dark:

```text
BackgroundBase    #0C1410
PanelSurface      #111A15
Foreground        #E7E9DF
Muted             #A2AA9F
Border            #344038
AccentPrimary     #DEB85D
AccentSecondary   #798477
```

Collapsed:

```text
260–300 px wide
66–78 px high
```

Example:

```text
┌────────────────────────────────┐
│ 03 WORKING      ! 01 NEEDS YOU │
│ ▁▂▃▅▆▅▃▅▇▆▅▃          60 MIN │
└────────────────────────────────┘
```

Expanded:

```text
┌──────────────────────────────────────┐
│ AGENT STATUS              03 / 01    │
│ ▁▂▃▅▆▅▃▅▇▆▅▃              60 MIN    │
├──────────────────────────────────────┤
│ 01  Aurora   Running tests       12m │
│ 02  Webots   Editing files       31m │
│ !3  TCS      Approval required    4m │
│ 04  SGLang   Completed           18m │
└──────────────────────────────────────┘
```

One shared `HudWindow.xaml`, two resource dictionaries:

```text
WebsiteLight.xaml
WebsiteDark.xaml
```

No duplicated layouts.

---

# 10. Voice Specification

Voice-pack events:

```text
permission_required
input_required
task_error
task_complete
all_tasks_complete
```

Voice-pack manifest:

```json
{
  "id": "example-pack",
  "name": "Example Pack",
  "language": "en",
  "version": 1,
  "events": {
    "permission_required": [
      "audio/permission_01.wav",
      "audio/permission_02.wav"
    ],
    "input_required": ["audio/input_01.wav"],
    "task_error": ["audio/error_01.wav"],
    "task_complete": ["audio/complete_01.wav"],
    "all_tasks_complete": ["audio/all_complete_01.wav"]
  }
}
```

Anti-annoyance:
- speak once while condition remains active;
- re-arm only after condition clears;
- completion aggregation window ~1.5–3 seconds;
- short per-event cooldown;
- persistent mute.

Generic TTS request:

```json
{
  "text": "TCS needs your approval.",
  "event": "permission_required",
  "task": "TCS"
}
```

Response:

```text
Content-Type: audio/wav
```

TTS failure must never block the HUD.

---

# 11. Git Practices

`main` must stay reviewed/runnable.

One branch per plan:

```text
impl/p1-monitoring
impl/p2-hud
impl/p3-history-voice
impl/p4-release
```

DeepSeek may make multiple logical commits inside a plan.

Required gate before Astra review:

```text
dotnet build
dotnet test
goal-specific smoke check
clean/understood git status
IMPLEMENTATION_STATE.md updated
```

No:
- force-push;
- rewriting `main`;
- broad `git clean -fdx`;
- unrelated resets;
- destructive branch cleanup.

The plan branch is reviewed as one coherent unit.

---

# 12. Automation Model

V1 uses exactly:

```text
4 plans
4 implementation goals
4 Astra review passes
```

A plan and a goal are the same execution unit for automation purposes.

Each cycle is:

```text
Astra plans the whole plan
    ↓
Astra writes .agent/HANDOFF.md
    ↓
DeepSeek executes autonomously until the plan goal is complete
    ↓
DeepSeek builds/tests/smoke-checks
    ↓
DeepSeek updates IMPLEMENTATION_STATE.md
    ↓
Astra reviews the whole plan diff
    ↓
DeepSeek automatically fixes blockers if needed
    ↓
Astra re-checks
    ↓
merge to main
```

There should be no normal human handoff inside the plan.

---

# 13. The “Sufficient Statistics” Handoff Rule

This is mandatory.

Astra is not allowed to merely say:

> “Implement Plan 2 according to the spec.”

Before DeepSeek starts, Astra must inspect the repository and produce an execution packet containing enough information that DeepSeek can act without re-planning the project.

The handoff packet must be written to:

```text
.agent/HANDOFF.md
```

and must contain the following sufficient statistics.

## 13.1 Plan identity

```text
Plan
Goal
Branch
Base commit
Current head commit
```

## 13.2 Objective

One concise statement of the end state.

Not a list of vague activities.

Example:

```text
Goal: transform the verified console monitor into a usable persistent WPF HUD
with collapsed/expanded views, Light/Dark website styling, tray behavior,
position persistence, task navigation, and monitoring-health UX.
```

## 13.3 Current repository facts

Astra must report what it actually observed, such as:

```text
existing projects
important namespaces
current interfaces
existing tests
build state
known warnings
relevant current files
current monitoring output shape
current config model
```

This prevents DeepSeek from spending a large part of the run rediscovering repo state.

## 13.4 Relevant code map

For the current plan only:

```text
READ:
- exact files that contain prerequisite logic

MODIFY:
- likely files to change

CREATE:
- expected new files

DO NOT TOUCH:
- stable components outside scope
```

## 13.5 Architecture invariants

Astra must restate only the invariants relevant to the current plan.

Example:

```text
UI consumes HudViewModel only.
Voice never parses Codex logs.
No old Monitor HUD presentation code.
Light/Dark share one layout tree.
Monitoring failure must not render as zero tasks.
```

## 13.6 Upstream reuse map

If relevant, Astra identifies:

```text
upstream file/path
what is reusable
what must be adapted
what must not be copied
known coupling risk
```

DeepSeek should not have to independently rediscover upstream architecture unless Astra marks an uncertainty.

## 13.7 Implementation checklist

A concrete ordered checklist internal to the one large goal.

This is not a new set of “goals”.

Example:

```text
1. create window shell
2. wire view model
3. implement collapse/expand
4. implement resource dictionaries
5. add task rows
6. add tray
7. add position persistence
8. add task open/focus
9. add monitoring-health state
10. test/smoke
```

DeepSeek may execute this autonomously.

## 13.8 Acceptance criteria

Objective pass/fail criteria for the plan.

## 13.9 Test matrix

Exact:
- unit tests to add/change;
- existing tests that must stay green;
- manual/smoke checks;
- commands to run.

## 13.10 Known risks and uncertainties

Only meaningful uncertainties.

For each:

```text
risk
expected handling
stop condition
```

DeepSeek should stop only for genuine architecture-invalidating discoveries.

## 13.11 Git contract

Astra specifies:
- plan branch;
- expected commit style;
- merge is forbidden before review;
- any files that should not be committed.

## 13.12 Direct DeepSeek execution prompt

The last section of `.agent/HANDOFF.md` must be:

```text
DEEPSEEK EXECUTION PROMPT
```

It must be directly executable and already include the relevant sufficient statistics.

DeepSeek should not need a human to rewrite it.

---

# 14. `AGENTS.md` Contract

The repository root must contain `AGENTS.md`.

Its purpose is to make the automation behavior persistent across runs.

It defines:
- Astra planner responsibilities;
- DeepSeek action responsibilities;
- Astra reviewer responsibilities;
- handoff packet requirements;
- branch/review gates;
- stop conditions;
- prohibition on scope expansion.

`AGENTS.md` is a control document, not a project diary.

A ready-to-use `AGENTS.md` accompanies this specification.

---

# 15. Plan 1 / Goal 1 — Foundation and Monitoring

Branch:

```text
impl/p1-monitoring
```

End state:

> Empty/private repo becomes a trustworthy normalized live Codex monitor with tests and a real-session debug harness.

DeepSeek handles in one autonomous run:
- repo/solution bootstrap;
- upstream monitoring extraction;
- licensing/provenance;
- config/logging skeleton;
- normalized `AgentTask`;
- state reducer;
- current-action classifier;
- monitoring health;
- approval/input observability;
- real-session debug harness;
- monitoring/state tests.

Required final debug shape:

```text
3 working · 1 needs you

#1 Aurora
WORKING
Running tests

#2 Webots
WORKING
Editing files

#3 TCS
NEEDS_APPROVAL
Waiting for approval
```

Astra Review 1 checks:
- upstream isolation;
- bounded monitoring;
- state semantics;
- approval/input truthfulness;
- failure handling;
- privacy;
- tests.

---

# 16. Plan 2 / Goal 2 — Complete Usable HUD

Branch:

```text
impl/p2-hud
```

End state:

> The verified console monitor becomes a desktop application that is already usable day-to-day.

DeepSeek handles:
- WPF shell;
- borderless drag behavior;
- topmost/opacity;
- position persistence;
- tray;
- startup toggle;
- collapsed summary;
- expanded task list;
- Light/Dark website-derived styling;
- procedural technical background;
- clipped-corner detail;
- task open/focus;
- monitoring-health UX;
- focus behavior.

Astra Review 2 checks:
- UI/core separation;
- focus stealing;
- theme architecture;
- website visual consistency;
- no dashboard creep;
- desktop lifecycle.

At the end of Plan 2, the user should be able to leave the app running.

---

# 17. Plan 3 / Goal 3 — Activity and Voice

Branch:

```text
impl/p3-history-voice
```

End state:

> The usable HUD gains the differentiating activity-history and voice behavior.

DeepSeek handles:
- 60-minute rolling history;
- sparkline;
- voice-pack loader;
- manifest validation;
- WAV playback;
- event routing;
- dedupe;
- re-arm;
- cooldown;
- completion aggregation;
- mute;
- generic TTS endpoint;
- timeout/cancellation;
- fallback behavior.

Astra Review 3 checks:
- spam prevention;
- thread safety;
- no UI blocking;
- correct event semantics;
- history correctness;
- resource use.

At the end of Plan 3, V1 is feature-complete.

---

# 18. Plan 4 / Goal 4 — Hardening and Release

Branch:

```text
impl/p4-release
```

End state:

> Feature-complete app becomes a robust packaged V1.

DeepSeek handles:
- sleep/wake;
- restart during active task;
- locked files;
- malformed/truncated JSONL;
- watcher failure;
- state DB unavailable;
- schema mismatch behavior;
- multi-monitor;
- removed monitor;
- mixed DPI;
- invalid voice pack;
- missing WAV;
- TTS timeout;
- completion bursts;
- regression tests;
- cleanup;
- publish;
- final ZIP;
- packaged smoke test;
- notices/readme.

Astra Review 4 checks:
- full spec compliance;
- failure behavior;
- tests;
- package;
- dependencies;
- provenance;
- dead code;
- release readiness.

Only after this review passes:

```text
merge
tag v1.0.0
```

---

# 19. Astra Planner Procedure

At the beginning of each plan, Astra must:

1. read `AGENTS.md`;
2. read this spec;
3. read `IMPLEMENTATION_STATE.md`;
4. inspect repo tree;
5. inspect `git status`;
6. inspect recent commits;
7. inspect relevant implementation files;
8. inspect relevant tests;
9. if upstream reuse is involved, inspect the specific upstream files;
10. determine the shortest correct implementation route;
11. write `.agent/HANDOFF.md`;
12. ensure the handoff contains the sufficient statistics defined above;
13. end the file with a direct DeepSeek execution prompt.

Astra planning is complete only when DeepSeek can begin without a human summarizing anything.

---

# 20. DeepSeek Action Procedure

DeepSeek must begin by reading:

```text
AGENTS.md
.agent/HANDOFF.md
docs/IMPLEMENTATION_STATE.md
```

It should read the full V1 spec only when:
- the handoff references a section;
- an ambiguity cannot be resolved from the handoff;
- it detects a potential architecture conflict.

DeepSeek then:

```text
inspect referenced code
→ implement whole plan goal
→ test incrementally
→ build
→ full test
→ smoke-check
→ update IMPLEMENTATION_STATE.md
→ leave branch review-ready
```

DeepSeek should not ask the human to manually relay context already present in the repo.

Stop only when:
- required observability does not exist;
- upstream assumption is false in an architecture-changing way;
- the plan requires scope expansion;
- destructive/unrelated repo state makes safe continuation impossible.

Otherwise, solve ordinary implementation issues autonomously.

---

# 21. Astra Review Procedure

Astra reviews the entire plan branch against `main`.

Read:
- `AGENTS.md`;
- spec;
- `IMPLEMENTATION_STATE.md`;
- `.agent/HANDOFF.md`;
- branch diff;
- tests/build output;
- relevant runtime result.

Severity:

```text
BLOCKER
MAJOR
MINOR
NOTE
```

Final decision:

```text
PASS
PASS WITH NONBLOCKING NOTES
CHANGES REQUIRED
```

If changes are required:
- Astra gives one compact ordered fix prompt;
- DeepSeek fixes on the same branch;
- build/tests/smoke rerun;
- Astra performs a short re-review;
- no new plan is created.

---

# 22. `IMPLEMENTATION_STATE.md`

Keep this compact.

Template:

```markdown
# Agent HUD Implementation State

## Current
Plan: P1 — Foundation and Monitoring
Goal: G1 — Trustworthy normalized live monitor
Branch: impl/p1-monitoring

## Plan Status
- Astra handoff: ready
- DeepSeek implementation: pending
- Build: pending
- Tests: pending
- Smoke: pending
- Astra review: pending

## Completed
- none

## Known Limitations
- input-request observability not yet characterized

## Blocking Issues
- none

## Next Action
- DeepSeek executes .agent/HANDOFF.md
```

---

# 23. Privacy and Performance Rules

Do not:
- persist full conversation text;
- log prompts;
- log assistant bodies;
- log full commands by default;
- send anything except short notification text to remote TTS.

Keep:
- incremental reads;
- bounded bytes;
- watcher + reconciliation;
- bounded recent-session discovery;
- no full history rescans every UI tick;
- no file scanning/HTTP on UI thread.

---

# 24. Testing Matrix

Monitoring:
- active session;
- stale session;
- duplicate rollout;
- sparse long-running session;
- lock;
- malformed record;
- monitoring degradation;
- restart recovery.

State:
- `WORKING → NEEDS_APPROVAL`;
- `NEEDS_APPROVAL → WORKING`;
- `WORKING → NEEDS_INPUT`;
- `NEEDS_INPUT → WORKING`;
- `WORKING → DONE`;
- `WORKING → ERROR`;
- duplicate/out-of-order event.

UI:
- collapse/expand;
- drag;
- position;
- Light/Dark;
- monitoring offline;
- task click;
- tray;
- focus behavior.

History:
- transition sample;
- 60-minute trim;
- constant/empty series.

Voice:
- valid/invalid pack;
- missing clip;
- speak once;
- re-arm;
- mute;
- TTS timeout;
- no UI freeze;
- completion aggregation.

---

# 25. V1 Acceptance Checklist

- [ ] packaged build launches directly;
- [ ] active Codex tasks detected;
- [ ] duplicate conversations deduplicated;
- [ ] normalized state is not merely upstream active/listening/idle;
- [ ] current-action labels useful;
- [ ] approval state works where observable;
- [ ] input state works where observable and limitations are documented;
- [ ] monitoring failure visible;
- [ ] collapsed HUD shows working + needs-human + sparkline;
- [ ] expanded HUD shows concise task rows;
- [ ] Light/Dark share one layout;
- [ ] visual treatment follows supplied website language;
- [ ] drag/position persistence works;
- [ ] tray/startup work;
- [ ] voice packs work;
- [ ] generic TTS endpoint works;
- [ ] mute works;
- [ ] voice spam prevented;
- [ ] sleep/wake tested;
- [ ] DPI/multi-monitor recovery tested;
- [ ] build/tests pass;
- [ ] upstream notices included;
- [ ] release ZIP smoke-tested.

---

# 26. Immediate Start Sequence

```text
1. initialize private repo;
2. add this file as docs/V1_2_IMPLEMENTATION_SPEC.md;
3. add AGENTS.md;
4. create docs/IMPLEMENTATION_STATE.md;
5. add the supplied website screenshots to design/reference/;
6. create impl/p1-monitoring;
7. run Astra planning for Plan 1;
8. Astra writes .agent/HANDOFF.md with sufficient statistics + DeepSeek prompt;
9. DeepSeek reads the handoff and executes Goal 1 autonomously;
10. Astra reviews the Plan 1 branch;
11. DeepSeek automatically fixes blockers if needed;
12. merge Plan 1;
13. repeat for Plans 2, 3, and 4.
```

There are only four normal human-visible execution cycles.

---

# 27. Final Product Standard

> A small technical instrument sits at the edge of the desktop. It is quiet when nothing matters. It shows whether agents are actually working. One click reveals what they are doing. When a human decision is required, it becomes visually obvious and—if enabled—speaks once. Then it gets out of the way.

That is the product.
