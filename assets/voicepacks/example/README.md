# Example voice pack (opt-in, synthetic)

These cues are original synthetic PCM tones generated for OVERSEER. They are not
speech, not recordings and not third-party assets: every clip is a short sine
tone produced by the same tone generator the voice smoke check uses
(`VoicePackValidator.CreateTone`, 22050 Hz mono 16-bit PCM).

| Event | Clips | Tone |
| --- | --- | --- |
| `permission_required` | `permission-1.wav`, `permission-2.wav` | 660 / 720 Hz, 0.18 s |
| `input_required` | `input.wav` | 600 Hz, 0.15 s |
| `task_error` | `error.wav` | 320 Hz, 0.22 s |
| `task_complete` | `complete.wav` | 880 Hz, 0.15 s |
| `all_tasks_complete` | `all.wav` | 990 Hz, 0.25 s |

## Install (opt-in)

1. Copy this directory to `%LOCALAPPDATA%\OVERSEER\voicepacks\example`.
2. In `%LOCALAPPDATA%\OVERSEER\config.json` set `"Provider": "pack"` and
   `"PackId": "example"` under `Voice`, then restart OVERSEER.
3. Unmute in the HUD (voice starts muted and the mute state is remembered).

OVERSEER never installs or replaces a pack by itself, and it refuses manifests,
clip paths and reparse points that leave the pack directory.
