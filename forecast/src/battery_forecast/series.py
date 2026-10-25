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

    @property
    def end(self) -> float:
        """Inizio dell'ultimo passo, quello che contiene l'istante attuale."""
        return self.start + (len(self.level) - 1) * STEP_SECONDS


def build(samples, now: float, level_now: float, ac_now: int | None,
          step: int = STEP_SECONDS, max_gap: int = MAX_GAP_STEPS,
          max_days: float = MAX_CONTEXT_DAYS) -> Series:
    """`samples`: sequenza di (ts UTC in secondi, livello 0–100 o None, ac 0/1 o None)."""
    arr = np.array([(float(t), np.nan if l is None else float(l), np.nan if a is None else float(a))
                    for t, l, a in samples] or np.empty((0, 3)), dtype=np.float64).reshape(-1, 3)
    ts, lv, ac = arr[:, 0], arr[:, 1], arr[:, 2]
    keep = np.isfinite(ts) & (ts <= now) & (ts > now - max_days * 86400)
    lv = np.where((lv >= 0) & (lv <= 100), lv, np.nan)          # fuori scala = lettura non valida
    ac = np.where((ac == 0) | (ac == 1), ac, np.nan)
    ts, lv, ac = ts[keep], lv[keep], ac[keep]
    order = np.argsort(ts, kind="stable")
    ts, lv, ac = ts[order], lv[order], ac[order]

    end_bin = int(now // step)
    first_bin = int(ts[0] // step) if len(ts) else end_bin
    n = end_bin - first_bin + 1
    bins = (ts // step).astype(np.int64) - first_bin

    # livello: media dei campioni validi del passo; alimentazione: ultimo valore noto del passo
    sums = np.bincount(bins, weights=np.nan_to_num(lv), minlength=n)
    counts = np.bincount(bins, weights=np.isfinite(lv).astype(float), minlength=n)
    level = np.full(n, np.nan)
    np.divide(sums, counts, out=level, where=counts > 0)
    acs = np.full(n, np.nan)
    valid_ac = np.isfinite(ac)
    acs[bins[valid_ac]] = ac[valid_ac]        # con indici ripetuti vince l'ultimo, cioè il più recente

    level[-1] = level_now
    if ac_now is not None:
        acs[-1] = float(ac_now)

    level = _fill_short_gaps(level, acs, max_gap)
    acs = _fill_short_gaps_ac(acs, level, max_gap)

    obs = np.flatnonzero(np.isfinite(level))
    span = (obs[-1] - obs[0]) if len(obs) else 0
    return Series(
        start=float((first_bin) * step),
        level=level.astype(np.float32),
        ac=acs.astype(np.float32),
        observed_days=round(span * step / 86400, 2),
        coverage=round(len(obs) / (span + 1), 3) if len(obs) else 0.0,
        discharge_sessions=_count_sessions(level, acs, min_steps=max(1, 1800 // step)),
    )


def _runs_of_nan(x: np.ndarray):
    """(inizio, fine esclusa) di ogni tratto di NaN interno, cioè con valori validi ai due lati."""
    isn = np.isnan(x)
    edges = np.diff(np.concatenate([[0], isn.astype(np.int8), [0]]))
    for a, b in zip(np.flatnonzero(edges == 1), np.flatnonzero(edges == -1)):
        if a > 0 and b < len(x):
            yield int(a), int(b)


def _fill_short_gaps(level: np.ndarray, ac: np.ndarray, max_gap: int) -> np.ndarray:
    """Interpolazione lineare dei buchi brevi, se l'alimentazione ai due lati è la stessa."""
    out = level.copy()
    for a, b in _runs_of_nan(level):
        if b - a > max_gap:
            continue
        if not (ac[a - 1] == ac[b] or (np.isnan(ac[a - 1]) and np.isnan(ac[b]))):
            continue                                            # in mezzo c'è stata una ricarica
        out[a:b] = np.interp(np.arange(a, b), [a - 1, b], [level[a - 1], level[b]])
    return out


def _fill_short_gaps_ac(ac: np.ndarray, level: np.ndarray, max_gap: int) -> np.ndarray:
    """Lo stato dell'alimentatore si propaga solo dentro i buchi brevi con lo stesso stato ai lati."""
    out = ac.copy()
    for a, b in _runs_of_nan(ac):
        if b - a <= max_gap and ac[a - 1] == ac[b]:
            out[a:b] = ac[b]
    return out


def _count_sessions(level: np.ndarray, ac: np.ndarray, min_steps: int) -> int:
    on_battery = (ac == 0) & np.isfinite(level)
    edges = np.diff(np.concatenate([[0], on_battery.astype(np.int8), [0]]))
    lengths = np.flatnonzero(edges == -1) - np.flatnonzero(edges == 1)
    return int((lengths >= min_steps).sum())


CONTEXT_RANGES = {
    "discharge_w": (0, 1000), "charge_w": (0, 1000), "cpu_pct": (0, 100),
    "cpu_mhz": (0, 20000), "memory_pct": (0, 100), "brightness_pct": (0, 100),
    "cpu_temp_c": (-30, 130), "battery_temp_c": (-30, 130), "thermal_zone_c": (-30, 130),
    "power_profile": (0, 3),
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
        keep = (bins >= 0) & (bins < n) & (arr[:, 0] <= context.end)
        bins, values = bins[keep], arr[:, 1][keep]
        if name == "power_profile":
            keep = values == np.floor(values)
            bins, values = bins[keep], values[keep]
        counts = np.bincount(bins, minlength=n)
        sums = np.bincount(bins, weights=values, minlength=n)
        channel = np.full(n, np.nan, dtype=np.float32)
        np.divide(sums, counts, out=channel, where=counts > 0)
        observed = np.isfinite(channel)
        if observed.sum() < 6 or np.nanstd(channel) < 1e-5:
            continue
        if name == "power_profile":
            # The C# bin is missing when multiple profiles were observed in that bin.
            categorical.append(channel)
        else:
            channel = _fill_short_gaps(channel, context.ac, MAX_GAP_STEPS)
            numeric.append(channel)
        names.append(name)
    return (np.asarray(numeric, dtype=np.float32)[None] if numeric else None,
            np.asarray(categorical, dtype=np.float64)[None] if categorical else None, names)
