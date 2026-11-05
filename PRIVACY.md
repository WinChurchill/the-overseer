# OVERSEER privacy

OVERSEER monitors local metadata and never answers an approval, sends an agent prompt
or changes harness execution. Codex monitoring opens local rollout files to parse
allowed event metadata. Optional DSH/Claude bridges receive host events/hooks and
select bounded metadata. Sensitive fields may exist in the input being parsed; they
are not copied into normalized records, history, voice notifications or diagnostics.

Local task views can show session titles, task identifiers and project paths. Bridge
spools hold bounded session/turn/request identifiers, timestamps, state/outcome and
project-root metadata. Do not treat spools or settings as anonymous data.

Settings, placement, bounded history/logs, spools and user recordings live in
`%LOCALAPPDATA%\OVERSEER`, or the selected data directory. The app folder is separate.
The app makes no telemetry or analytics request. This static project website has no
added analytics, trackers or external fonts; the eventual host may retain HTTP logs.

The optional Claude Projects desktop observer reads loaded accessibility metadata:
project name/ID, thread ID, status group and the dedicated displayed worker title.
Titles stay in the local task view; IDs determine identity and remain the fallback.
It never invokes controls, navigates Claude, reads chat feeds/edit values/message
snippets, extracts authentication or uses a browser relay. Aggregate card labels
are not read. Unexposed project states remain unknown.

**Voice starts muted.** New profiles select the included prerecorded Kokoro pack.
Local packs/recordings play locally, without a running model or network service.
Optional Windows speech synthesizes generic phrases using an installed voice.
If you select
and configure the optional **TTS endpoint** provider, the app sends generic notification
text, event code and a `Task N`/aggregate alias to your chosen endpoint. That endpoint
sees the request and your network address and has its own retention policy. Raw task
titles, prompts, answers and tool output are not included. Endpoint URLs/headers may
contain secrets and belong only in your local configuration; do not upload them.

There is no automatic cross-provider fallback that conceals failure. Mute and provider
failure remain visible. The local monitoring/privacy claims do not mean an opt-in
network provider is offline. Review provider choices before unmuting.

Exit the app and stop producers before deleting data. Remove hooks/plugins and startup
first when uninstalling; keep unrelated host settings. Removing the app folder does
not remove saved data. Before sharing evidence, sanitize names, paths, IDs and logs.
