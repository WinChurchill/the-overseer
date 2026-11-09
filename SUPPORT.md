# Support and bug reports

Use the [repository bug form](https://github.com/WinChurchill/the-overseer/issues/new?template=bug_report.yml)
for a public bug report. If you received a private test build, reply through the
channel that supplied it. The [tester handoff](docs/TESTER_HANDOFF.md) includes a
checklist, troubleshooting steps and a report template.

Include the source commit SHA and local-change status for a source build, or
application version, `release.json` source identity and ZIP SHA-256 for a package.
Also include Windows build, display scale, host/app
and engine versions, a short reproduction, expected/observed behavior and whether
an isolated data directory reproduces it. Record source-health reason codes if useful.
A sanitized screenshot is optional. Hide project names, paths and task identifiers.

Do not attach raw Codex rollouts, DSH/Claude sessions, prompts, credentials, full
settings/data directories or unreviewed logs. A security issue uses the private route
in [SECURITY.md](SECURITY.md) rather than a public issue. Experimental integrations may require a
specific Windows-local session environment; unsupported coverage is shown in the UI.
