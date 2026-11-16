# Security reporting

Use [GitHub private vulnerability reporting](https://github.com/WinChurchill/the-overseer/security/advisories/new).
If you received a private test build, you can also use the channel that supplied
it. Keep sensitive findings out of public issues.

Describe the affected version/hash, impact, minimal reproduction and relevant reason
codes. Use synthetic data and redact credentials and personal information.
Release notes will identify affected versions and the version with the fix.

OVERSEER is read-only toward harness execution. Optional network TTS and local data
are explained in PRIVACY.md. Read-only operation does not replace review of the files,
settings and local runtime that a bridge uses.

## Threat model

OVERSEER watches files that coding agents write. An agent can be prompt-injected,
so everything it writes is treated as hostile input. The goal is that a hostile
agent, repository or session file can at worst show a wrong status in the HUD. It
must not be able to run code, reach the network, or act on another application
through OVERSEER.

| Surface | What protects it |
| --- | --- |
| Network | The app opens no listening port. The only outbound call is the optional TTS endpoint you configure: HTTPS, or HTTP to your own machine. It sends fixed phrases such as "Task 2 needs your input", never titles, prompts or paths. |
| Agent session files and spools | Reads are bounded (window, line, record and JSON-depth caps, a per-pass byte budget, a capped number of sources). Identifiers are allowlisted. Symbolic links, junctions and hard-linked spool files are refused. Titles and folder names are stripped of control, zero-width and text-direction characters before display. |
| Claude Code hooks | The hook runs `node` with an argument list, not a shell string. It reads at most 1 MiB with a 2 second deadline and always exits 0 without printing. It never returns a permission decision. The installer only touches its own marked handlers. |
| DeepSeek Harness observer | Writes metadata to the spool. No network access, child processes or dynamic code. |
| Claude chat titles | Opens only `local_<id>.json` files named by a validated desktop id under the Claude app's session folder, refuses links and files over 1 MiB, and uses only the `title` string, cleaned and clipped. |
| Claude Projects reader | Reads the accessibility tree of `Claude.exe` windows only. It never clicks, types or changes them. |
| Opening a task | Builds `codex://threads/<id>` only when the id is letters, digits and dashes. No other text reaches the shell. |
| Startup | One quoted value under the current user's `Run` key. |
| Releases and CI | Workflows pin every action to a commit, default to read-only tokens and do not keep checkout credentials. Each release ZIP has a SHA-256 checksum and a GitHub build attestation. |

### Out of scope and known limits

- **Another program running as you.** It can already write the spool, register a
  fake `Claude.exe` or edit your settings. OVERSEER can't defend against your own
  account and does not try.
- **Fake status.** Anything able to write an agent's session files can make the
  HUD show a wrong status or title. Treat the HUD as a pointer, and act in the
  agent's own app.
- **Unsigned alpha ZIP.** Check the checksum or attestation before running it.
  Extract into a new, empty folder you own, not loose into Downloads: Windows looks
  for some DLLs next to the app first, so a stray DLL in the same folder could load.
- **Your Claude Code settings.** The hooks run inside Claude Code with your
  permissions. Review `~/.claude/settings.json` after installing or removing them.
