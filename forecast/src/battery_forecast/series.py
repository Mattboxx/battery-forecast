"""Dallo storico grezzo alla serie a 5 minuti che legge il modello.

Differenze volute rispetto a scripts/battery_now.py (macOS):
  - le lacune si interpolano solo fino a MAX_GAP_STEPS passi; oltre restano NaN, che Timestral
    tratta come valori mancanti (l'originale interpolava fino a 24 ore inventando uno storico);
  - lo stato dell'alimentatore ignoto resta NaN invece di diventare "scollegato";
  - l'ultimo passo vale il livello attuale, non la media del passo.
"""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np

from . import STEP_SECONDS

MAX_GAP_STEPS = 2            # buchi fino a 10 minuti: campioni persi, non sospensioni
MAX_CONTEXT_DAYS = 110       # oltre il contesto di Timestral-28M non serve inviare dati


@dataclass(frozen=True)
class Series:
    start: float                 # secondi UTC dell'inizio del primo passo
    level: np.ndarray            # float32, NaN = non osservato
    ac: np.ndarray               # float32, 1 = alimentatore, 0 = batteria, NaN = ignoto
    observed_days: float         # dal primo all'ultimo passo osservato
    coverage: float              # frazione di passi osservati in quell'intervallo
    discharge_sessions: int      # tratti continui a batteria lunghi almeno 30 minuti
    observed_hours: float = 0.0   # actual observed grid points, before interpolation
    origin: float | None = None
    charge_sessions: int = 0     # tratti continui con alimentatore, almeno 30 minuti e 2 punti di carica guadagnati
    observed: np.ndarray | None = None  # original observations, never interpolation/hold-only bins
    break_before: np.ndarray | None = None
    barrier_bins: np.ndarray | None = None  # partial bins that cannot separate both sides of a break

    @property
    def end(self) -> float:
        """Inizio dell'ultimo passo, quello che contiene l'istante attuale."""
        return self.start + (len(self.level) - 1) * STEP_SECONDS


def build(samples, now: float, level_now: float, ac_now: int | None,
          step: int = STEP_SECONDS, max_gap: int = MAX_GAP_STEPS,
          max_days: float = MAX_CONTEXT_DAYS, discontinuities=()) -> Series:
    """`samples`: sequenza di (ts UTC in secondi, livello 0–100 o None, ac 0/1 o None)."""
    arr = np.array([(float(row[0]), np.nan if row[1] is None else float(row[1]),
                     np.nan if row[2] is None else float(row[2]), float(row[3]) if len(row)>3 else 1.)
                    for row in samples] or np.empty((0, 4)), dtype=np.float64).reshape(-1, 4)
    ts, lv, ac, original = arr.T
    keep = np.isfinite(ts) & (ts <= now) & (ts > now - max_days * 86400)
    lv = np.where((lv >= 0) & (lv <= 100), lv, np.nan)          # fuori scala = lettura non valida
    ac = np.where((ac == 0) | (ac == 1), ac, np.nan)
    ts, lv, ac, original = ts[keep], lv[keep], ac[keep], original[keep]
    order = np.argsort(ts, kind="stable")
    ts, lv, ac, original = ts[order], lv[order], ac[order], original[order]

    end_bin = int(now // step)
    first_bin = int(ts[0] // step) if len(ts) else end_bin
    n = end_bin - first_bin + 1
    bins = (ts // step).astype(np.int64) - first_bin

    # C# sends one time-weighted value per bin; direct raw callers retain bin means.
    sums = np.bincount(bins, weights=np.nan_to_num(lv), minlength=n)
    counts = np.bincount(bins, weights=np.isfinite(lv).astype(float), minlength=n)
    level = np.full(n, np.nan)
    np.divide(sums, counts, out=level, where=counts > 0)
    acs = np.full(n, np.nan)
    ac_min, ac_max = np.full(n, np.inf), np.full(n, -np.inf)
    valid_ac = np.isfinite(ac)
    np.minimum.at(ac_min, bins[valid_ac], ac[valid_ac])
    np.maximum.at(ac_max, bins[valid_ac], ac[valid_ac])
    ac_count = np.bincount(bins, weights=valid_ac, minlength=n)
    all_count = np.bincount(bins, minlength=n)
    same = (all_count > 0) & (ac_count == all_count) & (ac_min == ac_max)
    acs[same] = ac_min[same]       # mixed/unknown states are not a single observed scenario

    breaks=np.zeros(n,dtype=bool)
    contaminated=np.zeros(n,dtype=bool)
    for t in discontinuities:
        if not np.isfinite(t) or t>now:
            continue
        index=int(np.floor(t/step))-first_bin
        if 0<=index<n:
            breaks[index]=True
            if t>(first_bin+index)*step:
                contaminated[index]=True
                if index+1<n: breaks[index+1]=True
    # A mean from a bin straddling an interruption has no unambiguous power phase.
    level[contaminated]=np.nan
    acs[contaminated]=np.nan

    level[-1] = level_now
    acs[-1] = float(ac_now) if ac_now in (0, 1) else np.nan
    original_counts=np.bincount(bins,weights=(original==1)&np.isfinite(lv),minlength=n)
    observed = np.isfinite(level) & (original_counts>0)
    observed[-1]=np.isfinite(level_now)  # the actual reading is always an observation

    acs = _fill_short_gaps_ac(acs, level, max_gap, breaks)
    level = _fill_short_gaps(level, acs, max_gap, breaks)

    obs = np.flatnonzero(observed)
    span = (obs[-1] - obs[0]) if len(obs) else 0
    return Series(
        start=float((first_bin) * step),
        level=level.astype(np.float32),
        ac=acs.astype(np.float32),
        observed_days=round(span * step / 86400, 2),
        coverage=round(len(obs) / (span + 1), 3) if len(obs) else 0.0,
        discharge_sessions=_count_sessions(np.where(observed,level,np.nan), acs, min_steps=max(1, 1800 // step), breaks=breaks),
        observed_hours=max(0, len(obs) - 1) * step / 3600,
        origin=now,
        charge_sessions=_count_charge_sessions(np.where(observed,level,np.nan), acs, min_steps=max(1, 1800 // step), breaks=breaks),
        observed=observed,break_before=breaks,barrier_bins=contaminated,
    )


def _runs_of_nan(x: np.ndarray):
    """(inizio, fine esclusa) di ogni tratto di NaN interno, cioè con valori validi ai due lati."""
    isn = np.isnan(x)
    edges = np.diff(np.concatenate([[0], isn.astype(np.int8), [0]]))
    for a, b in zip(np.flatnonzero(edges == 1), np.flatnonzero(edges == -1)):
        if a > 0 and b < len(x):
            yield int(a), int(b)


def _fill_short_gaps(level: np.ndarray, ac: np.ndarray, max_gap: int, breaks=None) -> np.ndarray:
    """Interpolazione lineare dei buchi brevi, se l'alimentazione ai due lati è la stessa."""
    out = level.copy()
    for a, b in _runs_of_nan(level):
        if b - a + 1 > max_gap:
            continue
        if breaks is not None and breaks[a:b+1].any():
            continue
        if not (np.isfinite(ac[a - 1]) and np.all(ac[a - 1:b + 1] == ac[a - 1])):
            continue                                            # in mezzo c'è stata una ricarica
        out[a:b] = np.interp(np.arange(a, b), [a - 1, b], [level[a - 1], level[b]])
    return out


def _fill_short_gaps_ac(ac: np.ndarray, level: np.ndarray, max_gap: int, breaks=None) -> np.ndarray:
    """Lo stato dell'alimentatore si propaga solo dentro i buchi brevi con lo stesso stato ai lati."""
    out = ac.copy()
    for a, b in _runs_of_nan(ac):
        if (b - a + 1 <= max_gap and ac[a - 1] == ac[b] and not np.isfinite(level[a:b]).any()
                and (breaks is None or not breaks[a:b+1].any())):
            out[a:b] = ac[b]
    return out


def _phase_runs(valid, breaks=None):
    cuts=np.zeros(len(valid),dtype=bool) if breaks is None else breaks
    starts=np.flatnonzero(valid & (np.r_[True,~valid[:-1]] | cuts))
    ends=np.flatnonzero(valid & (np.r_[~valid[1:],True] | np.r_[cuts[1:],False]))+1
    return zip(starts,ends)


def _count_charge_sessions(level: np.ndarray, ac: np.ndarray, min_steps: int, breaks=None) -> int:
    """Ricariche osservate: tratti con alimentatore lunghi abbastanza e in cui il livello è davvero salito."""
    plugged = (ac == 1) & np.isfinite(level)
    n = 0
    for a, b in _phase_runs(plugged,breaks):
        if b - a - 1 >= min_steps and np.nanmax(level[a:b]) - level[a] >= 2:
            n += 1
    return n


def _count_sessions(level: np.ndarray, ac: np.ndarray, min_steps: int, breaks=None) -> int:
    on_battery = (ac == 0) & np.isfinite(level)
    return int(sum(b-a-1>=min_steps for a,b in _phase_runs(on_battery,breaks)))


CONTEXT_RANGES = {
    "discharge_w": (0, 1000), "charge_w": (0, 1000), "cpu_pct": (0, 100),
    "cpu_mhz": (0, 20000), "memory_pct": (0, 100), "brightness_pct": (0, 100),
    "cpu_temp_c": (-30, 130), "battery_temp_c": (-30, 130), "thermal_zone_c": (-30, 130),
    "power_profile": (0, 3), "display_state": (0, 2),
}


def past_covariates(data: dict, context: Series):
    """Historical observations only, aligned with the battery grid. No invented future sensor values.

    Six observed bins and nonconstant data are required per channel. Numeric gaps of at most
    ten minutes interpolate only within the same observed power state. Profiles are categorical
    and never interpolated. Long gaps and unavailable sensors remain NaN.
    """
    numeric, categorical, names = [], [], []
    n = len(context.level)
    for name in CONTEXT_RANGES:
        pairs = data.get(name, [])
        if not pairs:
            continue
        arr = np.asarray([(t, np.nan if v is None else v) for t, v in pairs], dtype=np.float64)
        lower, upper = CONTEXT_RANGES[name]
        valid = np.isfinite(arr[:, 0]) & np.isfinite(arr[:, 1]) & (arr[:, 1] >= lower) & (arr[:, 1] <= upper)
        arr = arr[valid]
        if not len(arr):
            continue
        bins = np.floor((arr[:, 0] - context.start) / STEP_SECONDS).astype(np.int64)
        keep = (bins >= 0) & (bins < n) & (arr[:, 0] <= (context.origin if context.origin is not None else context.end))
        bins, values = bins[keep], arr[:, 1][keep]
        if name in {"power_profile", "display_state"}:
            keep = values == np.floor(values)
            bins, values = bins[keep], values[keep]
        counts = np.bincount(bins, minlength=n)
        sums = np.bincount(bins, weights=values, minlength=n)
        channel = np.full(n, np.nan, dtype=np.float32)
        np.divide(sums, counts, out=channel, where=counts > 0)
        if name in {"power_profile", "display_state"}:
            # Never average categorical states when a caller supplies raw observations.
            minimum, maximum = np.full(n, np.inf), np.full(n, -np.inf)
            np.minimum.at(minimum, bins, values)
            np.maximum.at(maximum, bins, values)
            channel[minimum != maximum] = np.nan
        observed = np.isfinite(channel)
        if context.barrier_bins is not None:
            channel[context.barrier_bins]=np.nan
            observed=np.isfinite(channel)
        if observed.sum() < 6 or np.nanstd(channel) < 1e-5:
            continue
        if name in {"power_profile", "display_state"}:
            # The C# bin is missing when multiple profiles were observed in that bin.
            categorical.append(channel)
        else:
            channel = _fill_short_gaps(channel, context.ac, MAX_GAP_STEPS, context.break_before)
            numeric.append(channel)
        names.append(name)
    return (np.asarray(numeric, dtype=np.float32)[None] if numeric else None,
            np.asarray(categorical, dtype=np.float64)[None] if categorical else None, names)
