"""Prova reale con Timestral: richiede i pesi in forecast/models (tools/fetch_model.py).

Si salta se il modello manca, così i test ordinari non scaricano nulla. Lo storico è sintetico e
dichiarato tale: verifica il funzionamento, non l'accuratezza.
"""
import json
import os
import subprocess
import sys
import time
from pathlib import Path

import numpy as np
import pytest

ROOT = Path(__file__).resolve().parents[1]
MODEL = Path(os.environ.get("BATTERY_FORECAST_MODEL", ROOT / "models" / "Timestral-28M"))
pytestmark = pytest.mark.skipif(not (MODEL / "config.json").is_file(), reason="pesi Timestral non presenti")

NOW = 1_800_000_000.0


def synthetic_days(days=7, now=NOW):
    """Ogni giorno: carica fino al 100% di notte, poi ~8%/h dalle 9 per 6 ore, poi alimentatore."""
    out = []
    for d in range(days, 0, -1):
        base = now - d * 86400
        for m in range(0, 24 * 60, 5):
            t = base + m * 60
            h = m / 60
            if 9 <= h < 15:
                out.append((t, 100 - 8 * (h - 9), 0))
            else:
                out.append((t, 100.0 if h < 9 else 52.0 + min(48, 30 * (h - 15)), 1))
    return out


def request(level=60.0, samples=None, **kw):
    return {"type": "forecast", "protocol_version": 1, "id": 1, "now_utc": NOW, "level": level, "ac": 0,
            "reserve_pct": 5, "samples": [list(s) for s in (samples if samples is not None else synthetic_days())], **kw}


@pytest.fixture(scope="module")
def engine():
    from battery_forecast.engine import Engine
    return Engine(MODEL, threads=2)


def test_forecast_is_physical_and_complete(engine):
    from battery_forecast import protocol
    r = engine.predict(protocol.parse_request(json.dumps(request()).encode()))
    curve = np.array([[p["p10"], p["p50"], p["p90"]] for p in r["curve"]])
    assert len(curve) == 288
    assert (curve <= 60).all() and (curve >= 0).all()
    assert (np.diff(curve, axis=0) <= 1e-9).all() and (np.diff(curve, axis=1) >= -1e-9).all()
    assert r["curve"][0]["t"] > NOW
    assert r["quality"]["discharge_sessions"] == 7
    assert r["crossing"]["status"] in {"within_horizon", "beyond_horizon"}
    if r["crossing"]["central_utc"]:
        assert NOW < r["crossing"]["central_utc"] <= r["curve"][-1]["t"]
    assert r["model_revision"] != "unknown"


def test_empty_history_still_answers(engine):
    from battery_forecast import protocol
    r = engine.predict(protocol.parse_request(json.dumps(request(samples=[])).encode()))
    assert r["quality"]["state"] == "insufficient"
    assert len(r["curve"]) == 288


def test_worker_process_round_trip_and_exit_on_eof():
    env = {**os.environ, "PYTHONPATH": str(ROOT / "src"), "PYTHONUTF8": "1"}
    p = subprocess.Popen([sys.executable, "-m", "battery_forecast", "--model", str(MODEL)],
                         stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=env)
    try:
        ready = json.loads(p.stdout.readline())
        assert ready["type"] == "ready", ready
        p.stdin.write(b"not json\n" + json.dumps(request()).encode() + b"\n")
        p.stdin.flush()
        err = json.loads(p.stdout.readline())
        assert err["type"] == "error" and err["code"] == "bad_json"
        t0 = time.perf_counter()
        ok = json.loads(p.stdout.readline())
        assert ok["type"] == "forecast" and ok["id"] == 1
        print(f"\nstartup {ready['startup_ms']} ms, round trip {1000 * (time.perf_counter() - t0):.0f} ms, "
              f"interno {ok['elapsed_ms']} ms")
        p.stdin.close()
        assert p.wait(timeout=20) == 0
    finally:
        if p.poll() is None:
            p.kill()
