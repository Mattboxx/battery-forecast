"""Discharge curve from the forecast drain (drain.py): parsing, integration and engine output."""
import json

import numpy as np
import pytest

from battery_forecast import drain, protocol

NOW = 1_800_000_000.0


class FakeForecaster:
    """Returns a constant drain for every quantile, spread by ±50% from p10 to p90."""
    quantile_levels = [.1, .2, .3, .4, .5, .6, .7, .8, .9]

    def __init__(self, rate):
        self.rate, self.seen = rate, None

    def forecast(self, context, prediction_length):
        self.seen = context
        spread = np.linspace(.5, 1.5, len(self.quantile_levels))
        return _Result(np.tile(self.rate * spread, (prediction_length, 1))[None, None])


class _Result:
    def __init__(self, q):
        self.quantiles = _Tensor(q)


class _Tensor:
    def __init__(self, q):
        self.q = q

    def __getitem__(self, index):
        return _Tensor(self.q[index])

    def numpy(self):
        return self.q


def pairs(n, rate=12.0):
    return [[NOW - 300 * (n - 1 - i), rate] for i in range(n)]


def test_parse_keeps_valid_past_drain_only():
    out = drain.parse([[NOW - 600, 10], [NOW - 300, None], [NOW - 200, 0], [NOW - 100, 500], [NOW + 300, 10]], NOW)
    assert out == [(NOW - 600, 10.0)]
    with pytest.raises(ValueError):
        drain.parse([[NOW, float("nan")]], NOW)
    with pytest.raises(ValueError):
        drain.parse([[True, 1]], NOW)


def test_protocol_rejects_bad_drain_and_defaults_to_none():
    base = {"type": "forecast", "protocol_version": 1, "id": 1, "now_utc": NOW, "level": 50, "samples": []}
    assert protocol.parse_request(json.dumps(base).encode())["drain"] == []
    with pytest.raises(protocol.ProtocolError):
        protocol.parse_request(json.dumps({**base, "drain_pct_per_hour": [[NOW, "x"]]}).encode())


def test_too_little_drain_gives_no_curve():
    assert drain.curves(FakeForecaster(10), drain.parse(pairs(drain.MIN_STEPS - 1), NOW), 80, 24) is None


def test_curve_integrates_the_forecast_drain():
    fc = FakeForecaster(12.0)
    q = drain.curves(fc, drain.parse(pairs(3000), NOW), 80, 24)
    assert fc.seen.shape == (1, 1, drain.MAX_STEPS)                 # at most a week of time on battery
    # 12%/h on average: one point per 5-minute step, 12 points per hour
    assert q[11, 1] == pytest.approx(80 - 12)
    assert q[23, 1] == pytest.approx(80 - 24)
    # the band: p10 uses the fast drain (1.5×), p90 the slow one (0.5×)
    assert q[11, 0] == pytest.approx(80 - 18) and q[11, 2] == pytest.approx(80 - 6)
    assert (np.diff(q, axis=0) <= 0).all()


def test_curve_never_goes_below_empty():
    q = drain.curves(FakeForecaster(100.0), drain.parse(pairs(100, rate=100), NOW), 20, 24)
    assert (q >= 0).all() and q[-1, 1] == 0


def test_central_drain_ignores_the_extreme_tail_quantiles():
    levels = np.array([.01, .05, .1, .3, .5, .7, .9, .95, .99])
    q = np.array([[1, 2, 10, 11, 12, 13, 14, 60, 200.0]])           # a rare peak stretches only the top quantiles
    assert drain.central(q, levels)[0] == pytest.approx(12, abs=.2)
    assert q.mean() > 30                                              # the plain mean would follow the peak
    # too few central quantiles: the plain mean
    assert drain.central(np.array([[4.0, 8.0]]), np.array([.05, .95]))[0] == 6
