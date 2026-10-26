"""Logica senza modello: serie, vincoli fisici, attraversamento della riserva e protocollo."""
import json

import numpy as np
import pytest

from battery_forecast import physics, protocol, series

NOW = 1_800_000_000.0          # multiplo di 300: l'istante cade all'inizio di un passo
STEP = 300


def samples(start, minutes, level0, slope_per_min, ac=0, every=60):
    return [(start + m * 60, level0 + slope_per_min * m, ac) for m in range(0, minutes, every // 60)]


class TestSeries:
    def test_last_step_is_current_level(self):
        s = series.build(samples(NOW - 3600, 60, 80, -0.1), NOW + 120, 70.0, 0)
        assert s.level[-1] == 70.0
        assert s.end <= NOW + 120 < s.end + STEP

    def test_long_gap_stays_missing_and_short_gap_is_filled(self):
        a = samples(NOW - 10 * 3600, 60, 90, -0.1)                   # 1 ora
        b = samples(NOW - 6 * 3600, 60, 80, -0.1)                    # dopo 3 ore di sospensione
        c = [x for x in samples(NOW - 3600, 60, 70, -0.1) if not 20 <= (x[0] - (NOW - 3600)) / 60 < 25]
        s = series.build(a + b + c, NOW, 64.0, 0)
        assert np.isnan(s.level).sum() >= 30                          # la sospensione resta un buco
        tail = s.level[-12:]
        assert not np.isnan(tail).any()                               # estremi distanti 10 minuti: interpolati
        assert s.coverage < 1

    def test_gap_across_charge_is_not_interpolated(self):
        a = [(NOW - 1800, 40.0, 0)]
        b = [(NOW - 1200, 60.0, 1)]
        s = series.build(a + b, NOW, 60.0, 1)
        i0 = int((NOW - 1800) // STEP - s.start // STEP)
        assert np.isnan(s.level[i0 + 1])

    def test_unknown_ac_is_not_turned_into_battery(self):
        s = series.build([(NOW - 3000, 50.0, None), (NOW - 2700, 49.0, None)], NOW, 45.0, None)
        assert np.isnan(s.ac).all()

    def test_invalid_levels_and_future_samples_are_dropped(self):
        s = series.build([(NOW - 600, 255.0, 0), (NOW - 300, -1.0, 0), (NOW + 600, 10.0, 0)], NOW, 50.0, 0)
        assert np.isnan(s.level[:-1]).all() and s.level[-1] == 50.0

    def test_duplicates_are_averaged_but_mixed_ac_is_unknown(self):
        s = series.build([(NOW - 300, 50.0, 0), (NOW - 300, 52.0, 1)], NOW, 50.0, 1)
        assert s.level[-2] == 51.0 and np.isnan(s.ac[-2])

    def test_sessions_and_days(self):
        day1 = samples(NOW - 86400 - 7200, 120, 100, -0.2)
        day2 = samples(NOW - 7200, 120, 100, -0.2)
        s = series.build(day1 + day2, NOW, 76.0, 0)
        assert s.discharge_sessions == 2
        assert s.observed_days == pytest.approx(1.08, abs=0.02)

    def test_empty_history(self):
        s = series.build([], NOW, 50.0, 0)
        assert len(s.level) == 1 and s.observed_days == 0 and s.discharge_sessions == 0


class TestPhysics:
    def test_constraint_monotone_bounded_and_ordered(self):
        q = np.array([[60, 55, 70], [65, 50, 40], [-5, 20, 110]], dtype=float)
        c = physics.constrain_no_charge(q, 58.0)
        assert (c <= 58).all() and (c >= 0).all()
        assert (np.diff(c, axis=0) <= 0).all()
        assert (np.diff(c, axis=1) >= 0).all()

    def test_crossing_within_horizon_interpolates_and_rounds(self):
        times = NOW + STEP * np.arange(1, 5)
        c = physics.crossing(np.array([20, 10, 0, 0.]), times, 5.0, 30.0, NOW)
        assert c.status == "within_horizon"
        assert c.at == NOW + 2.5 * STEP - 30          # a metà del secondo passo, arrotondato al minuto

    def test_habitual_recharge_needs_a_sustained_rise(self):
        assert physics.habitual_recharge(np.array([60, 58, 59, 57, 55.]), 62) is None      # rumore di 1 punto
        assert physics.habitual_recharge(np.array([60, 58, 61, 57, 55.]), 62) is None      # un solo passo
        assert physics.habitual_recharge(np.array([60, 55, 50, 53, 56, 70.]), 62) == 3

    def test_extension_continues_from_base_with_band(self):
        q = np.array([[50, 52, 54], [48, 50, 52], [48, 50, 52], [48, 50, 52.]])
        e = physics.extend_after(q, 2, 1.0, q[1])
        assert e[2].tolist() == [46.75, 49.0, 51.25] and e[3].tolist() == [45.5, 48.0, 50.5]
        assert (e[:2] == q[:2]).all()

    def test_crossing_states(self):
        times = NOW + STEP * np.arange(1, 4)
        assert physics.crossing(np.array([50, 40, 30.]), times, 5, 60, NOW).status == "beyond_horizon"
        assert physics.crossing(np.array([3, 2, 1.]), times, 5, 4, NOW).status == "already_below"


class TestProtocol:
    def base(self, **kw):
        return {"type": "forecast", "protocol_version": 1, "id": 7, "now_utc": NOW, "level": 50,
                "ac": 0, "samples": [[NOW - 300, 51, 0]], **kw}

    def test_parse_defaults(self):
        r = protocol.parse_request(json.dumps(self.base()).encode())
        assert r["reserve"] == 5 and r["horizon"] == 288 and r["samples"][0][1] == 51

    @pytest.mark.parametrize("bad,code", [
        (b"{nope", "bad_json"),
        (json.dumps({"type": "forecast", "protocol_version": 99}).encode(), "bad_version"),
        (json.dumps({"type": "forecast", "protocol_version": 1, "id": 1}).encode(), "bad_request"),
    ])
    def test_rejects(self, bad, code):
        with pytest.raises(protocol.ProtocolError) as e:
            protocol.parse_request(bad)
        assert e.value.code == code

    def test_rejects_out_of_range(self):
        with pytest.raises(protocol.ProtocolError):
            protocol.parse_request(json.dumps(self.base(level=120)).encode())

    def test_encode_has_no_nan_and_one_line(self):
        b = protocol.encode({"type": "x", "v": float("nan"), "w": [float("inf")]})
        assert b.endswith(b"\n") and b.count(b"\n") == 1
        assert json.loads(b) == {"protocol_version": 1, "type": "x", "v": None, "w": [None]}
