"""Motore finto per i test del client C#: parla il protocollo senza PyTorch.

  python fake_worker.py <modalità>

  ok        risponde con una curva lineare
  slow      pronto, ma non risponde mai alle previsioni (timeout)
  crash     pronto, poi esce alla prima richiesta
  noisy     molto stderr, risposta spezzata in più scritture, righe non JSON
  badmodel  segnala modello mancante ed esce
"""
import json
import sys
import time

mode = sys.argv[1] if len(sys.argv) > 1 else "ok"
out = sys.stdout


def send(msg):
    out.write(json.dumps({"protocol_version": 1, **msg}) + "\n")
    out.flush()


if mode == "badmodel":
    send({"type": "error", "id": None, "code": "model_unavailable", "message": "modello non trovato"})
    sys.exit(2)

send({"type": "ready", "model_id": "fake", "model_revision": "0", "startup_ms": 1})
for line in sys.stdin:
    req = json.loads(line)
    if mode == "slow":
        continue
    if mode == "crash":
        sys.exit(3)
    if mode == "noisy":
        sys.stderr.write("Warning: rumore\n" * 2000)
        out.write("non json\n")
    now, level = req["now_utc"], req["level"]
    curve = [{"t": now + 300 * i, "p10": max(0, level - 1.2 * i), "p50": max(0, level - i), "p90": max(0, level - 0.8 * i)}
             for i in range(1, req["horizon_steps"] + 1)]
    reply = json.dumps({"protocol_version": 1, "type": "forecast", "id": req["id"], "generated_at_utc": time.time(),
                        "context_end_utc": now, "source": "fake", "model_id": "fake", "model_revision": "0",
                        "scenario": "no_charge", "reserve_pct": req["reserve_pct"], "level": level,
                        "quality": {"state": "ok", "observed_days": len(req["samples"]) / 288,
                                    "coverage": 1, "discharge_sessions": 1},
                        "crossing": {"status": "within_horizon", "central_utc": now + 300 * (level - 5),
                                     "early_utc": None, "late_utc": None},
                        "curve": curve, "elapsed_ms": 1}) + "\n"
    if mode == "noisy":
        out.write(reply[:10]); out.flush(); time.sleep(0.05); out.write(reply[10:])
    else:
        out.write(reply)
    out.flush()
