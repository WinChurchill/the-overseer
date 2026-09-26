# OVERSEER integrations

Optional, opt-in bridges that let OVERSEER show and announce work from other agent
runtimes. They are shipped inside the release archive under `integrations/` and are
never required to run OVERSEER.

| Directory | Harness | Runtime needed | Status |
| --- | --- | --- | --- |
| `deepseek-harness/` | DeepSeek Harness (`dsh`) | Node.js 20 or newer, in the host that runs `dsh` | Contract-tested; **not yet qualified against a live host** |
| `claude-code/` | Claude Code | Node.js 18 or newer | Contract-tested; **not yet qualified against a live CLI** |

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
never moves, orphans or rebinds an installed bridge, and `--data-directory PATH`
keeps an isolated run completely separate from your real one.

## Setup and removal

Follow the README in each integration directory. Both installers preview by default,
write only OVERSEER-owned entries, back up any file they modify, and are reversible:

- `integrations/deepseek-harness/README.md` — install the observer plugin into your
  `dsh` profile bundle.
- `integrations/claude-code/README.md` — `node install.mjs <settings.json>` /
  `node uninstall.mjs <settings.json>`.

**Restart the host after installing or removing a bridge.** OVERSEER discovers spool
directories when it composes its monitor at startup, so a new bridge is picked up at
the next OVERSEER restart; hot attach is not implemented and is not claimed.

## Verifying a shipped copy

Both suites run from an extracted archive with no repository checkout:

```powershell
cd integrations/deepseek-harness; node --test
cd integrations/claude-code; node --test
```
