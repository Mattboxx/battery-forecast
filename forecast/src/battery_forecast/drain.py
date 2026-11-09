"""Discharge curve from a Timestral forecast of the drain (percent per hour), not of the level.

On a laptop the level falls exactly as fast as the energy is used, so the uncertain part of a discharge is the
power, not the shape of the curve. Timestral forecasts the drain series of this PC (only time on battery, in
5-minute steps) and the level follows by integration. Measured on real discharges this was much closer to the
actual arrival at the reserve than forecasting the level directly, whose forecast slowed down over time.
"""
from __future__ import annotations

import math

import numpy as np

from . import STEP_SECONDS

MIN_STEPS = 12          # at least an hour of measured drain on battery
MAX_STEPS = 2016        # a week of time on battery is enough context
MAX_RATE = 200.0        # %/h: above this a reading is not a discharge
FLOOR = 0.05            # %/h: the level never stops falling in the "no charge" scenario
CENTRAL = (0.1, 0.9)    # quantile levels that make up the central drain (see central())


def parse(pairs, now: float):
    """[[utc_seconds, pct_per_hour_or_null], ...] → (timestamps, values), validated and up to `now`."""
    if not isinstance(pairs, list) or len(pairs) > 40000:
        raise ValueError("expected a list of at most 40000 [utc_seconds, value] pairs")
    out = []
    for pair in pairs:
        if not isinstance(pair, (list, tuple)) or len(pair) != 2 or isinstance(pair[0], bool):
            raise ValueError("expected [utc_seconds, value_or_null]")
        t = float(pair[0])
        v = None if pair[1] is None else float(pair[1])
        if not math.isfinite(t) or (v is not None and not math.isfinite(v)):
            raise ValueError("nonfinite drain observation")
        if t <= now and v is not None and 0 < v <= MAX_RATE:
            out.append((t, v))
    out.sort()
    return out


def battery_series(pairs) -> np.ndarray | None:
    """The drain on battery, oldest first: charging and pauses are simply absent (time on battery only)."""
    if len(pairs) < MIN_STEPS:
        return None
    return np.asarray([v for _, v in pairs[-MAX_STEPS:]], dtype=np.float32)


def central(q: np.ndarray, levels: np.ndarray) -> np.ndarray:
    """The expected drain of each step from its quantiles, without the extreme tails.

    A plain mean of the quantile values counts the 1%, 5%, 95% and 99% quantiles as much as the others, and rare
    power peaks stretch the upper ones: on real discharges that put the arrival at the reserve about five minutes
    too early. The mean over the 10%-90% quantiles, weighted by the spacing of their levels, was unbiased on every
    target measured (the reserve, the next 10, 20 and 40 points) and closer on average."""
    inner = (levels >= CENTRAL[0] - 1e-9) & (levels <= CENTRAL[1] + 1e-9)
    if inner.sum() < 2:
        return q.mean(axis=1)
    weights = np.gradient(levels)[inner]
    return (q[:, inner] * weights).sum(axis=1) / weights.sum()


def curves(fc, pairs, level: float, horizon: int) -> np.ndarray | None:
    """(horizon, 3) levels for p10/p50/p90 from the drain forecast, or None without enough measured drain.

    p50 integrates the central drain (see central()); p10 integrates the high drain quantile and p90 the low
    one, step by step: a wide, cautious band."""
    s = battery_series(pairs)
    if s is None:
        return None
    q = fc.forecast(s[None, None], prediction_length=horizon).quantiles[0, 0].numpy()
    q = np.maximum(q.astype(np.float64), FLOOR)
    levels = np.asarray(fc.quantile_levels, dtype=np.float64)
    fast, slow = q[:, int(np.argmin(abs(levels - .9)))], q[:, int(np.argmin(abs(levels - .1)))]
    hours = STEP_SECONDS / 3600
    out = np.stack([level - np.cumsum(r * hours) for r in (fast, central(q, levels), slow)], axis=1)
    return np.clip(out, 0, 100)
