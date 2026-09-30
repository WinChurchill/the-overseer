# OVERSEER integrations

Optional, opt-in bridges that let OVERSEER show and announce work from other agent
runtimes. They are shipped inside the release archive under `integrations/` and are
never required to run OVERSEER.

| Directory | Harness | Runtime needed | Status |
| --- | --- | --- | --- |
| `deepseek-harness/` | DSH Desktop / DeepSeek Harness | Node.js 20 or newer in the host; Desktop bundles Node | Contract-tested; **not yet qualified against a live desktop session** |
| `claude-code/` | Claude Code desktop Code tab / editor / CLI | Node.js 18 or newer in the session environment | Contract-tested; **not yet qualified against a live Code session** |

Both integrations are **experimental**. Neither has been exercised end to end against a
live host on this machine, so what follows is contract behaviour and documentation, not
a compatibility claim for any host version.

## What a bridge does

A bridge subscribes to its host's documented status events and copies allowlisted
**metadata only** — session identity, turn and tool boundaries, approval and input
request ids, plan-mode changes and explicit outcomes — into a local spool file.
Prompt text, tool arguments, tool output, file contents and credentials are never
read or written. OVERSEER reads the spool as plain files; it never imports host code
and never influences an approval or input decision.

## Where the data goes

The spool lives **inside OVERSEER's data directory**
(`%LOCALAPPDATA%\OVERSEER\bridges\<harness>\<instance>` for `dsh`,
`%LOCALAPPDATA%\OVERSEER\spool\claude\<instance>` for Claude), not inside the
extracted application folder. Installing or removing an OVERSEER version therefore
never moves or orphans an installed bridge's **data**, and `--data-directory PATH`
keeps an isolated run completely separate from your real one.

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

The installed apps are available for testing; bridge activation and real session
state transitions still need to be observed. A missing standalone CLI on PATH
does not establish that a desktop/editor host is unavailable.

**Restart the host after installing or removing a bridge.** OVERSEER discovers spool
directories when it composes its monitor at startup, so a new bridge is picked up at
the next OVERSEER restart; hot attach is not implemented and is not claimed.

## Verifying a shipped copy

Both suites run from an extracted archive with no repository checkout:

```powershell
cd integrations/deepseek-harness; node --test
cd integrations/claude-code; node --test
```

The Claude suite includes installer regressions that delete a bridge from a simulated
extraction and prove that `--apply` refuses, that rebinding to a new directory whose
path contains spaces and non-ASCII characters rewrites every owned handler, and that
the command the settings file records actually publishes a record when it is invoked.

Original bridge code is MIT licensed; each bridge directory includes LICENSE.
