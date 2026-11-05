# Kokoro English notification recordings

Nine short WAV recordings generated locally from OVERSEER's fixed event catalog.
Select **Light & voice → Use included Kokoro voice**, then unmute when ready.
Assigned recordings retain precedence. Selection and opening settings are silent.

The app plays these WAVs through its existing voice-pack provider. It requires no
Python, model download, inference process or network service at playback time.
User packs with the same ID take precedence; malformed overrides report failure.

Generation uses `scripts/Generate-KokoroPack.py`, local Kokoro v1.0 ONNX assets,
kokoro-onnx 0.6.1, CPU inference with two threads, `af_heart`, en-us, speed 0.96.
`source.json` records exact scripts, model/voice SHA-256, clip hashes and durations.
Only the generated recordings are shipped; model weights and inference packages
are not included. These are synthetic speech, not recordings of a person.

Sources checked November 2, 2026:
- [Kokoro model card and Apache-2.0 model license](https://huggingface.co/hexgrad/Kokoro-82M)
- [kokoro-onnx implementation and MIT license](https://github.com/thewh1teagle/kokoro-onnx)

To regenerate, use an existing environment with the stated packages and pass
`--model-directory` and a **new** `--output-directory` to the generator. Networking
is disabled by the script. Review changed audio and provenance before replacing
the shipped files. Validate WAVs using OVERSEER's existing voice-pack check.
