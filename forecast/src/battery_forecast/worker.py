"""Processo persistente: carica il modello, poi risponde a una richiesta per riga finché stdin è aperto.

  python -m battery_forecast --model <cartella>     servizio (l'app lo avvia così)

Su Windows non serve controllare il processo padre (l'originale guardava getppid() == 1): l'app
chiude stdin per fermarlo, e se l'app muore il Job Object di Windows termina anche questo processo.
"""
from __future__ import annotations

import argparse
import os
import sys
import time
import traceback

from . import protocol


def serve(model_dir: str, threads: int | None) -> int:
    out = sys.stdout.buffer
    sys.stdout = sys.stderr            # qualsiasi print di librerie va su stderr, non nel protocollo
    t0 = time.perf_counter()
    try:
        from .engine import Engine
        engine = Engine(model_dir, threads)
    except Exception as e:
        traceback.print_exc()
        out.write(protocol.error("model_unavailable", f"{type(e).__name__}: {e}"))
        out.flush()
        return 2
    out.write(protocol.encode({"type": "ready", "model_id": engine.model_id,
                               "model_revision": engine.model_revision,
                               "startup_ms": round((time.perf_counter() - t0) * 1000)}))
    out.flush()

    for line in sys.stdin.buffer:      # bloccante; finisce alla chiusura di stdin (EOF)
        if not line.strip():
            continue
        rid = None
        try:
            req = protocol.parse_request(line)
            rid = req["id"]
            if req["type"] == "ping":
                msg = protocol.encode({"type": "pong", "id": rid})
            else:
                msg = protocol.encode(engine.predict(req))
        except protocol.ProtocolError as e:
            msg = protocol.error(e.code, str(e), rid, e.retryable)
        except Exception as e:
            traceback.print_exc()
            msg = protocol.error("forecast_failed", f"{type(e).__name__}: {e}", rid, retryable=True)
        out.write(msg)
        out.flush()
    return 0


def main(argv=None) -> int:
    ap = argparse.ArgumentParser(prog="battery_forecast")
    ap.add_argument("--model", default=os.environ.get("BATTERY_FORECAST_MODEL"), help="cartella con config.json e pesi")
    ap.add_argument("--threads", type=int, default=int(os.environ.get("BATTERY_FORECAST_THREADS", "2")))
    args = ap.parse_args(argv)
    if not args.model:
        ap.error("serve --model o la variabile BATTERY_FORECAST_MODEL")
    return serve(args.model, args.threads)
