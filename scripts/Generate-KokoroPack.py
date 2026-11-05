"""Generate the fixed OVERSEER cue catalog using existing local Kokoro assets.

Run with a Python environment that already contains kokoro-onnx and onnxruntime.
No packages or models are downloaded. Output must be a new directory.
"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import socket
import wave


def no_network(*args, **kwargs):
    raise RuntimeError("Kokoro cue generation is local only")


socket.socket.connect = no_network
socket.socket.connect_ex = no_network
os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1",
                  HF_HUB_DISABLE_TELEMETRY="1")


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-directory", type=Path, required=True)
    parser.add_argument("--output-directory", type=Path, required=True)
    args = parser.parse_args()

    # This narrow extractor intentionally fails if the catalog format changes.
    root = Path(__file__).resolve().parent.parent
    catalog = root / "src/TheOverseer.Core/Voice/VoiceEventCatalog.cs"
    pairs = re.findall(
        r'new VoiceEventDefinition\([^,]+,\s*"([a-z_]+)",\s*"[^"\n]*",\s*"[^"\n]*",\s*"([^"\n]+)"',
        catalog.read_text(encoding="utf-8"),
    )
    if len(pairs) != 9 or len(dict(pairs)) != len(pairs):
        raise ValueError("Expected the complete nine-event catalog")
    model_path = args.model_directory / "kokoro-v1.0.onnx"
    voices_path = args.model_directory / "voices-v1.0.bin"
    if not model_path.is_file() or not voices_path.is_file():
        raise ValueError("Both local model assets must already exist")

    import numpy as np
    import onnxruntime as ort
    from kokoro_onnx import Kokoro

    options = ort.SessionOptions()
    options.intra_op_num_threads = 2
    options.inter_op_num_threads = 1
    options.log_severity_level = 3
    session = ort.InferenceSession(str(model_path), options,
                                  providers=["CPUExecutionProvider"])
    model = Kokoro.from_session(session, str(voices_path))
    output = args.output_directory.resolve()
    output.mkdir(parents=True, exist_ok=False)
    provenance = {"generator": "Generate-KokoroPack.py", "voice": "af_heart",
                  "speed": 0.96, "language": "en-us",
                  "modelSha256": sha256(model_path),
                  "voicesSha256": sha256(voices_path), "clips": {}}
    for code, script in pairs:
        samples, rate = model.create(script, voice="af_heart", speed=0.96,
                                     lang="en-us")
        if rate != 24000 or samples.ndim != 1 or not np.isfinite(samples).all():
            raise ValueError("Invalid local synthesis")
        if not 0.05 <= len(samples) / rate <= 15 or not np.any(samples):
            raise ValueError("Silent or out-of-bounds synthesis")
        pcm = (np.clip(samples, -1, 1) * 32767).astype("<i2")
        filename = code + ".wav"
        destination = output / filename
        with wave.open(str(destination), "wb") as wav:
            wav.setnchannels(1)
            wav.setsampwidth(2)
            wav.setframerate(rate)
            wav.writeframes(pcm.tobytes())
        provenance["clips"][code] = {"script": script,
            "durationMs": round(len(samples) * 1000 / rate),
            "sha256": sha256(destination)}
        print(f"{code}: {len(samples) / rate:.2f}s", flush=True)
    manifest = {"id": "kokoro-en", "name": "Kokoro English",
                "language": "en", "version": 1,
                "events": {code: [code + ".wav"] for code, _ in pairs}}
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    (output / "source.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
