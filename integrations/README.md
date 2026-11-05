# OVERSEER integrations

Optional, opt-in bridges that let OVERSEER show and announce work from other agent
runtimes. They are shipped inside the release archive under `integrations/` and are
never required to run OVERSEER.

| Directory | Harness | Runtime needed | Status |
| --- | --- | --- | --- |
| `deepseek-harness/` | DSH Desktop / DeepSeek Harness | Node.js 20 or newer in the host; Desktop bundles Node | Contract-tested; observer 1.1.0 targets Desktop 0.2.0-rc.2; full live-session qualification pending |
| `claude-code/` | Claude Code desktop Code tab / editor / CLI | Node.js 18 or newer in the session environment | Contract-tested; full live Code-session qualification pending |

Both integrations are **experimental**. Partial owner observations do not establish
complete end-to-end compatibility. Record your actual host/engine versions and
wait/resume outcomes using the [tester handoff](../docs/TESTER_HANDOFF.md).

### Claude Projects and cloud workers

Claude's redesigned Projects can coordinate several Claude Code worker threads.
Cloud threads do not inherit the hooks installed on this Windows machine; the
current OVERSEER bridge observes the local hook spool only. A zero local count
does not establish that cloud projects are idle. Projects with local workers and
cloud workers must be distinguished by execution location, not by the UI tab name.
See [Projects and execution location](https://code.claude.com/docs/en/claude-projects#what-threads-pick-up-from-your-repositories).

An **opt-in Windows desktop preview** reads loaded Projects **Threads / Overview**
panes through the accessibility interface. Enable it in OVERSEER's Projects & sources
panel and restart OVERSEER. It reads provider project/thread links and status group
headings plus the dedicated worker-title text on primary thread links. IDs remain
the stable identity and fallback; renaming changes no state, timing or notification.
It never navigates Claude, invokes controls or reads chat feeds, edit values,
aggregate card labels or message snippets. No browser login, cookies or cloud API key is used.

Working/Landing groups map to work; Waiting on you/Ready for review map to human
attention. Idle/Resolved are settled observations, not proof of successful work.
Disconnected or missing workers retain qualified state. Newly attached or reappearing
workers are silent; only an observed later status change can raise attention.
The coordinator is not counted as another worker. Code-hook execution identities
and project-thread identities have no qualified cross-source alias map yet.

The reader inspects up to four app-owned windows using one persistent accessibility
client, including loaded background views, without taking keyboard focus. The
Projects directory exposes names but no worker states. Background reading was
verified across eight polls of two Idle workers with Claude out of the foreground;
no notifications replayed. A closed/unmounted view cannot be recovered from the index.
Repeated card/artifact links count once; conflicting statuses stay unconfirmed,
including at the session limit. Live attention transitions still need qualification.

This preview covers exposed status only. Account-wide membership, closed projects,
collapsed/virtualized rows, execution liveness and host approvals remain unverified.
Coverage always stays partial; no all-completed claim can come from this observer.
One background reader, a one-second wait, two-second poll and the existing shared
byte/session/source budgets bound it. An inaccessible or blocked UI cannot hold the
HUD or enqueue more readers. Locale/schema changes reduce coverage instead of being
interpreted as an empty account. Actual Nov 2 observation: two waiting workers and one
disconnected idle worker; fixed tests use invented IDs/titles from that structure.

## What a bridge does

A bridge subscribes to its host's documented status events and copies allowlisted
**metadata only** — session identity, turn and tool boundaries, approval and input
request ids, plan-mode changes and explicit outcomes — into a local spool file.
Prompt text, tool arguments, tool output, file contents and credentials are not
copied into normalized spool records. OVERSEER reads the spool as plain files; it never imports host code
and never influences an approval or input decision.

## Where the data goes

By default, the spool lives **inside OVERSEER's default data directory**
(`%LOCALAPPDATA%\OVERSEER\bridges\<harness>\<instance>` for `dsh`,
`%LOCALAPPDATA%\OVERSEER\spool\claude\<instance>` for Claude), not inside the
extracted application folder. Installing or removing an OVERSEER version therefore
never moves or orphans an installed bridge's **data**. The app's `--data-directory PATH`
changes its own settings/history/log location; it does **not** redirect producers or
isolate source input. Use the bridge guide's spool overrides and the matching consumer
source root for an isolated bridge test. Keep this distinction when reporting results.

## Setup and removal

Keep the integration directory somewhere permanent before installing it. Claude
settings record an absolute bridge path; local-path DSH bundles can also depend
on their original location. A temporary test extraction is unsuitable.

Follow the README belonging to the actual host:

- [DSH Desktop / DeepSeek Harness](deepseek-harness/README.md): use Desktop's
  Plugins page, or its bundled CLI with `--profile desktop` while Desktop is fully
  quit. A separately npm-installed terminal DSH must not manage that profile.
  Terminal profiles use their own matching manager. Remove with the same manager.
- [Claude Code](claude-code/README.md): desktop/editor local sessions share Code
  hook settings. `node install.mjs <settings.json>` previews; `--apply` backs up and
  writes only OVERSEER entries after an isolated executable probe. Uninstall
  preserves unrelated settings. Check the session environment's Node/PATH and
  settings location; WSL/SSH/cloud spool routing is not qualified here.

A missing standalone CLI on PATH does not establish that a desktop/editor host is
unavailable. Check the installed host and the environment where its hooks actually run.

**Restart the host and OVERSEER after installing, removing or rebinding a bridge.** OVERSEER discovers spool
directories when it composes its monitor at startup, so a new bridge is picked up at
the next OVERSEER restart; hot attach is not implemented and is not claimed.

## State and attention contract

Each adapter owns its harness parsing and source-local state. The shared bridge
reducer accepts only the configured harness and source instance; task identity
includes both. Composition shares fixed resource budgets and combines normalized
facts. The HUD does not parse host events or infer answers from message text.

| Harness | Work and input rules | Recovery and limits |
| --- | --- | --- |
| Codex | Rollout lifecycle controls work. Async input can coexist with Working. A correlated reply clears only its question; unrelated text does not. Existing terminal/new-turn boundaries clear requests. Closing a prompt has no observed cancellation event. | After the existing 15-minute lease, an older async question becomes unconfirmed without inventing an answer; its ID remains for late replies. Fresh work is independent. Hydration is silent. Approval detection remains Unsupported. |
| DSH | Qualified `dsh-observer/1.1.0` questions remain pending across turn completion/new turns until a correlated answer or authoritative snapshot clears them. Approvals take priority. | Heartbeats prove host liveness, not continued action. Authoritative current state restores coverage; history gaps remain disclosed. Other producer versions do not claim the qualified question protocol. |
| Claude | `UserPromptSubmit`/`Stop` start/end the parent response. Child start/stop are liveness only. Question tools correlate by tool-use ID; MCP elicitation uses its elicitation ID. Unidentified requests survive unrelated work until a turn/session boundary. | Event-driven hooks cannot prove liveness during quiet periods. Missing decision IDs cannot establish a matching answer. Atomic accounting receipts retain drop evidence within the existing 250 ms fail-open budget. |

An unresolved request remains visible in Now when its status becomes unconfirmed,
with a qualifier. Confirmed counts/history exclude stale, provisional or
unconfirmed state; partial counts describe only confirmed members. Fresh bridge
request events can still notify when source membership is incomplete, while
backlog hydration and repeated publications stay silent. A failed peer cannot
erase another source's confirmed input request.

Bridge request tracking retains at most 64 identities per request kind, plus
fixed flags for missing identities/overflow. Overflow lowers coverage and cannot
be cleared by unrelated answers. Codex retains its existing configured request
cap and flags eviction as unconfirmed attention until a turn boundary. Existing file, byte, source, lease and deadline
limits are unchanged. These are regression-tested contracts; actual-host/manual
qualification remains separate.

Opaque wire identities have a 128-character cap. Producers encode oversized host
IDs as full SHA-256 tokens and escape literal `sha256-` IDs; they never truncate
identities. The reader rejects oversized/noncanonical identities in events and
current state. Retired source epochs cannot update state, ordering, producer version
or freshness. The collector keeps 64 retirement tombstones per source attachment;
on saturation, new epochs stay unconfirmed until OVERSEER restarts. Tombstones are
not discarded when session lanes are evicted.

Sixteen fixed producer timelines live in `fixtures/behavior/`: Claude C1–C4,
DSH D1–D4, and shared S1–S4 for each harness. They cover independent requests,
auxiliary activity, approval priority, failures, timed/blocking replies, metadata
coverage, ID boundaries, retired epochs, recovery/restart and partial writes/rotation.
The .NET scenario suite checks state, qualifiers, freshness, every count, Now,
project totals and exact attention edges through the production adapters and HUD.
Claude has no authoritative current-state producer; its recovery document is
explicit consumer-protocol fault injection. Other fixture events/current state
come from the shipped producer modules with private-content getters that throw.
Run `node integrations/fixtures/behavior/generate.mjs --check` from the repository
root to detect drift; omit `--check` only when intentionally updating fixtures.

## Verifying a shipped copy

Both suites run from an extracted archive with no repository checkout. For these
tests use Node.js 24 and PowerShell 7. Start at the **repository or extracted archive
root** and run the suites sequentially; check each exit status before continuing:

```powershell
Push-Location integrations/deepseek-harness
try { node --test --test-concurrency=1 } finally { Pop-Location }
Push-Location integrations/claude-code
try { node --test --test-concurrency=1 } finally { Pop-Location }
```

The Claude suite includes installer regressions that delete a bridge from a simulated
extraction and prove that `--apply` refuses, that rebinding to a new directory whose
path contains spaces and non-ASCII characters rewrites every owned handler, and that
the command the settings file records actually publishes a record when it is invoked.

Original bridge code is MIT licensed; each bridge directory includes LICENSE.
