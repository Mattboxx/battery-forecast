"""Scenario di carica: vincoli, arrivo all'obiettivo, stacco abituale e prova con Timestral reale."""
import json
import os
from pathlib import Path

import numpy as np
import pytest

from battery_forecast import physics, protocol, series

NOW = 1_800_000_000.0
STEP = 300
ROOT = Path(__file__).resolve().parents[1]
MODEL = Path(os.environ.get("BATTERY_FORECAST_MODEL", ROOT / "models" / "Timestral-28M"))


class TestChargePhysics:
    def test_constraint_never_drops_below_now_or_above_full(self):
        q = np.array([[55, 60, 58], [50, 70, 90], [101, 40, 99]], dtype=float)
        c = physics.constrain_charge(q, 57.0)
        assert (c >= 57).all() and (c <= 100).all()
        assert (np.diff(c, axis=0) >= 0).all()
        assert (np.diff(c, axis=1) >= 0).all()

    def test_crossing_up_interpolates_and_states(self):
        times = NOW + STEP * np.arange(1, 5)
        c = physics.crossing_up(np.array([70, 76, 84, 90.]), times, 80.0, 64.0, NOW)
        assert c.status == "within_horizon" and c.at == NOW + 2.5 * STEP - 30
        assert physics.crossing_up(np.array([60, 61.]), times[:2], 80, 59, NOW).status == "beyond_horizon"
        assert physics.crossing_up(np.array([82, 83.]), times[:2], 80, 81, NOW).status == "already_reached"

    def test_habitual_unplug_needs_a_sustained_drop(self):
        assert physics.habitual_unplug(np.array([60, 65, 64, 66, 70.]), 58) is None
        assert physics.habitual_unplug(np.array([60, 70, 66, 62, 50.]), 58) == 2

    def test_extension_slows_down_near_full(self):
        q = np.array([[60, 62, 64], [60, 62, 64], [0, 0, 0], [0, 0, 0], [0, 0, 0.]])
        e = physics.extend_charge_after(q, 2, 10.0, np.array([60, 62, 64.]))
        steps = np.diff(e[1:, 1])
        assert (steps > 0).all() and (np.diff(steps) < 0).all()      # sale sempre meno
        assert (e[2:] < 100).all() and (e[2:, 0] <= e[2:, 1]).all() and (e[2:, 1] <= e[2:, 2]).all()


def test_protocol_accepts_charge_with_target():
    base = {"type": "forecast", "protocol_version": 1, "id": 1, "now_utc": NOW, "level": 50, "ac": 1, "samples": []}
    r = protocol.parse_request(json.dumps({**base, "scenario": "charge", "target_pct": 90}).encode())
    assert r["scenario"] == "charge" and r["target"] == 90
    with pytest.raises(protocol.ProtocolError):
        protocol.parse_request(json.dumps({**base, "scenario": "charge", "target_pct": 30}).encode())


def charging_history(days=7, now=NOW):
    """Ogni sera: a batteria dalle 9 alle 19 fino al 30% circa, poi in carica: ~40%/h fino all'80%, poi ~10%/h."""
    out, level = [], 100.0
    for d in range(days, 0, -1):
        base = now - d * 86400
        for m in range(0, 24 * 60, 5):
            h = m / 60
            if 9 <= h < 19:
                level = max(3.0, level - 7 / 12)
                out.append((base + m * 60, level, 0))
            else:
                level = min(100.0, level + (40 if level < 80 else 10) / 12)
                out.append((base + m * 60, level, 1))
    return out


def test_series_counts_charge_sessions():
    s = series.build(charging_history(3), NOW, 50.0, 1)
    assert s.charge_sessions >= 3


@pytest.mark.skipif(not (MODEL / "config.json").is_file(), reason="pesi Timestral non presenti")
def test_real_timestral_charge_forecast_is_physical():
    from battery_forecast.engine import Engine
    engine = Engine(MODEL, threads=2)
    hist = charging_history(7, now=NOW - 30 * 60)
    hist += [(NOW - 30 * 60 + m * 60, 30 + 40 * m / 60, 1) for m in range(0, 30, 5)]      # ricarica in corso
    req = {"type": "forecast", "protocol_version": 1, "id": 1, "now_utc": NOW, "level": 50.0, "ac": 1,
           "scenario": "charge", "target_pct": 80, "samples": [list(x) for x in hist]}
    r = engine.predict(protocol.parse_request(json.dumps(req).encode()))
    curve = np.array([[p["p10"], p["p50"], p["p90"]] for p in r["curve"]])
    assert r["scenario"] == "charge" and r["target_pct"] == 80 and r["reserve_pct"] == 80
    assert (curve >= 50).all() and (curve <= 100).all()
    assert (np.diff(curve, axis=0) >= -1e-9).all() and (np.diff(curve, axis=1) >= -1e-9).all()
    assert r["quality"]["charge_sessions"] >= 7
    c = r["crossing"]
    assert c["status"] in {"within_horizon", "beyond_horizon"}
    if c["central_utc"]:
        assert NOW < c["central_utc"] and (c["early_utc"] is None or c["early_utc"] <= c["central_utc"])
        print(f"\nobiettivo 80% fra {(c['central_utc'] - NOW) / 60:.0f} min "
              f"(fascia {((c['early_utc'] or NOW) - NOW) / 60:.0f}–{((c['late_utc'] or NOW) - NOW) / 60:.0f}); "
              f"stacco abituale {r['habitual_unplug_utc'] and round((r['habitual_unplug_utc'] - NOW) / 60)} min")
