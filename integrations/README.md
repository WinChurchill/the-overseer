# OVERSEER integrations

Optional, opt-in bridges that let OVERSEER show and announce work from other agent
runtimes. They are shipped inside the release archive under `integrations/` and are
never required to run OVERSEER.

| Directory | Harness | Runtime needed | Status |
| --- | --- | --- | --- |
| `deepseek-harness/` | DeepSeek Harness (`dsh`) | Node.js 20 or newer, in the host that runs `dsh` | Contract-tested; **not yet qualified against a live host** |
| `claude-code/` | Claude Code | Node.js 18 or newer | Contract-tested; **not yet qualified against a live CLI** |

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

The **integration files are a different matter**, and the two hosts differ:

| | `claude-code` | `deepseek-harness` |
| --- | --- | --- |
| What the host stores | an absolute path to `bridge.mjs` in your settings file | a `link:` dependency, materialised as a directory junction |
| If you delete the directory you installed from | the hook command dangles and Claude Code reports a hook error | `dsh` still starts but **skips the bundle** with a warning on stderr |
| How to recover | re-run `install.mjs --bridge=<new path> --apply` | re-run `dsh plugin --profile <name> add "<new path>"` |

Neither host copies the bridge into a private location, so **keep the extracted
integration directory somewhere permanent** (for example under your user profile)
instead of installing from a temporary extraction you intend to delete.

## Setup and removal

Follow the README in each integration directory. Both write only OVERSEER-owned
entries, back up any file they modify, and are reversible:

- `integrations/claude-code/README.md` — `node install.mjs <settings.json>` previews;
  `--apply` writes. It **refuses** to write unless the hook command it is about to
  record has been executed successfully against a throwaway spool, so a settings file
  can never be pointed at a missing or non-runnable bridge.
- `integrations/deepseek-harness/README.md` — install the observer plugin into your
  `dsh` profile bundle with the host's own `dsh plugin` command.

**There is no preview command for the `dsh` integration.** `dsh plugin` forwards its
arguments to `pnpm` and declares no dry-run flag of its own, so a bare-path install
links the directory immediately. Read the two files named in that README before you
run it; that is the closest thing to a preview the host offers.

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

