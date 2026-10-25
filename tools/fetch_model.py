"""Scarica i pesi di Timestral a una revisione fissata, per lo sviluppo e per il pacchetto.

  forecast/.venv/Scripts/python tools/fetch_model.py [--model 28M|5M] [--revision SHA] [--out DIR]

Scrive DIR/MODEL_INFO.json con id, revisione risolta e hash SHA-256 dei file: il motore li riporta in
ogni risposta e il pacchetto li verifica. È l'unico punto in cui si usa la rete per il modello.
"""
import argparse
import hashlib
import json
from pathlib import Path

from huggingface_hub import HfApi, snapshot_download

ROOT = Path(__file__).resolve().parents[1]
# Revisioni provate il 2026-10-25 (docs/UPSTREAM.md).
PINNED = {"28M": "49b26409ac87b5935539a5d8ffe7f397b720db47", "5M": "bdd20cfc15a3fea0e4cb5ccef37f8d3ca7aa335f"}


def sha256(p: Path) -> str:
    h = hashlib.sha256()
    with p.open("rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", choices=sorted(PINNED), default="28M")
    ap.add_argument("--revision")
    ap.add_argument("--out", type=Path)
    a = ap.parse_args()
    repo = f"mistralai/Timestral-{a.model}"
    rev = a.revision or PINNED[a.model] or HfApi().model_info(repo).sha
    out = a.out or ROOT / "forecast" / "models" / f"Timestral-{a.model}"
    snapshot_download(repo, revision=rev, local_dir=out)
    files = {p.relative_to(out).as_posix(): sha256(p) for p in sorted(out.rglob("*"))
             if p.is_file() and ".cache" not in p.parts and p.name != "MODEL_INFO.json"}
    info = {"model_id": repo, "revision": rev, "files": files}
    (out / "MODEL_INFO.json").write_text(json.dumps(info, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({"model_id": repo, "revision": rev, "dir": str(out), "files": list(files)}, indent=2))


if __name__ == "__main__":
    main()
