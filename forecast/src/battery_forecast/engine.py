"""Timestral caricato una volta, da cartella locale: nessun download dentro l'app."""
from __future__ import annotations

import json
import os
import time
from pathlib import Path

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"

import numpy as np

from . import STEP_SECONDS, physics, series

# Soglie di prodotto provvisorie: da ritarare con la validazione (docs/VALIDATION.md).
MIN_DAYS_LEARNING = 3.0
MIN_HOURS_USABLE = 2.0


def _continuous_rate(s: series.Series, ac_state: int, steps: int) -> float | None:
    """Robust slope of the current contiguous power phase, not separate visits in the last hour."""
    lv, ac = s.level[-steps:], s.ac[-steps:]
    valid = np.isfinite(lv) & (ac == ac_state)
    if not len(valid) or not valid[-1]:
        return None
    stop = np.flatnonzero(~valid)
    start = int(stop[-1]) + 1 if len(stop) else 0
    if s.break_before is not None:
        boundaries=np.flatnonzero(s.break_before[-steps:])
        if len(boundaries): start=max(start,int(boundaries[-1]))
    indices=np.arange(len(lv))[start:]
    if s.observed is not None:
        indices=indices[s.observed[-steps:][indices]]
    if len(indices) < max(3, steps // 2):
        return None
    # Pairwise slopes suppress a single rounded or noisy endpoint. No future sensor assumptions.
    slopes = [(float(lv[j]) - float(lv[i])) / ((j-i)*STEP_SECONDS/3600)
              for i in indices for j in indices if j-i>=2]
    if not slopes:
        return None
    slope = float(np.median(slopes))
    return slope if ac_state == 1 else -slope


def observed_rate(s: series.Series, steps: int = 12) -> float | None:
    return _continuous_rate(s, 0, steps)


def observed_charge_rate(s: series.Series, steps: int = 12) -> float | None:
    return _continuous_rate(s, 1, steps)


def quality_state(s: series.Series, scenario: str = "no_charge") -> str:
    sessions = s.charge_sessions if scenario == "charge" else s.discharge_sessions
    if s.observed_hours < MIN_HOURS_USABLE or sessions == 0:
        return "insufficient"
    return "learning" if s.observed_days < MIN_DAYS_LEARNING else "ok"


def model_info(model_dir: Path) -> tuple[str, str]:
    """(id, revisione) dal file MODEL_INFO.json scritto da tools/fetch_model.py."""
    try:
        info = json.loads((model_dir / "MODEL_INFO.json").read_text(encoding="utf-8"))
        return info["model_id"], info["revision"]
    except (OSError, ValueError, KeyError):
        return model_dir.name, "unknown"


class Engine:
    def __init__(self, model_dir: str | os.PathLike, threads: int | None = None):
        import torch
        from timestral import TimestralForecaster

        model_dir = Path(model_dir)
        if not (model_dir / "config.json").is_file():
            raise FileNotFoundError(f"modello non trovato in {model_dir}")
        if threads:
            torch.set_num_threads(threads)
        self._torch = torch
        self.fc = TimestralForecaster.from_pretrained(str(model_dir), local_files_only=True)
        self.model_id, self.model_revision = model_info(model_dir)
        lv = np.array(self.fc.quantile_levels)
        self.qi = [int(np.argmin(abs(lv - .1))), int(self.fc.median_index), int(np.argmin(abs(lv - .9)))]

    @staticmethod
    def _no_charge(raw: np.ndarray, level: float, s: series.Series):
        """Scenario senza ricarica: vincoli fisici e, oltre la ricarica abituale, estrapolazione dichiarata."""
        plug = physics.habitual_recharge(raw[:, 1], level)
        q = physics.constrain_no_charge(raw, level)
        if plug is None:
            return q, None, None
        rate = None
        if plug >= 6:                                  # almeno 30 minuti di previsione prima della ricarica
            rate = (level - q[plug - 1, 1]) / (plug * STEP_SECONDS / 3600)
        if rate is None or rate < physics.MIN_RATE:
            rate = observed_rate(s)
        if rate is None or rate < physics.MIN_RATE:
            return q, plug, None                       # nessun ritmo affidabile: si dice, non si inventa
        base = q[plug - 1] if plug > 0 else np.full(3, level)
        q = physics.extend_after(q, plug, rate * STEP_SECONDS / 3600, base)
        return physics.constrain_no_charge(q, level), plug, rate

    @staticmethod
    def _charge(raw: np.ndarray, level: float, s: series.Series):
        """Scenario "resto collegato": vincoli fisici e, oltre lo stacco abituale, avvicinamento al pieno dichiarato."""
        unplug = physics.habitual_unplug(raw[:, 1], level)
        q = physics.constrain_charge(raw, level)
        if unplug is None:
            return q, None, None
        rate = None
        if unplug >= 3:                                 # almeno 15 minuti di carica prevista prima dello stacco
            rate = (q[unplug - 1, 1] - level) / (unplug * STEP_SECONDS / 3600)
        if rate is None or rate < physics.MIN_RATE:
            rate = observed_charge_rate(s)
        if rate is None or rate < physics.MIN_RATE:
            return q, unplug, None
        base = q[unplug - 1] if unplug > 0 else np.full(3, level)
        q = physics.extend_charge_after(q, unplug, rate * STEP_SECONDS / 3600, base)
        return physics.constrain_charge(q, level), unplug, rate

    def predict(self, req: dict) -> dict:
        t0 = time.perf_counter()
        s = series.build(req["samples"], req["now"], req["level"], req["ac"],discontinuities=req.get("discontinuities",()))
        h = req["horizon"]
        charge = req["scenario"] == "charge"
        # Covariata nota del futuro: lo scenario. "no_charge" = alimentatore scollegato, "charge" = collegato.
        past, categories, context_features = series.past_covariates(req.get("past_covariates", {}), s)
        active_level,active_ac,active_mask,past,categories=series.active_inputs(s,past,categories)
        known = np.concatenate([active_ac, np.full(h, 1.0 if charge else 0.0, np.float32)])
        with self._torch.inference_mode():
            q = self.fc.forecast(active_level[None, None], prediction_length=h,
                                 observation_mask=active_mask[None,None],
                                 known_covariates=known[None, None], past_covariates=past,
                                 categorical_past_covariates=categories).quantiles[0, 0].numpy()
        # The current anchor is the request time, not the beginning of its historical bin.
        times = req["now"] + STEP_SECONDS * np.arange(1, h + 1, dtype=np.float64)
        if charge:
            q, change, rate = self._charge(q[:, self.qi], req["level"], s)
            threshold = req["target"]
            # in carica arriva prima la curva più alta (p90) e per ultima la più bassa (p10)
            up = [physics.crossing_up(q[:, k], times, threshold, req["level"], req["now"]) for k in range(3)]
            cross = [up[2], up[1], up[0]]
        else:
            q, change, rate = self._no_charge(q[:, self.qi], req["level"], s)
            threshold = req["reserve"]
            cross = [physics.crossing(q[:, k], times, threshold, req["level"], req["now"]) for k in range(3)]
        plug = change
        return {
            "type": "forecast",
            "id": req["id"],
            "generated_at_utc": time.time(),
            "context_end_utc": req["now"],
            "source": "timestral",
            "model_id": self.model_id,
            "model_revision": self.model_revision,
            "scenario": req["scenario"],
            # soglia della curva: riserva in scarica, obiettivo in carica
            "reserve_pct": threshold,
            "target_pct": req["target"] if charge else None,
            "level": req["level"],
            "context_features": context_features,
            "time_basis": "active_use",
            "quality": {
                "model_context_steps":len(active_level),
                "calendar_context_steps":len(s.level),
                "state": quality_state(s, req["scenario"]),
                "observed_days": s.observed_days,
                "observed_hours": s.observed_hours,
                "coverage": s.coverage,
                "discharge_sessions": s.discharge_sessions,
                "charge_sessions": s.charge_sessions,
            },
            # p50 = stima centrale; early/late = primo e ultimo arrivo alla soglia fra p10 e p90 (in scarica
            # p10 arriva prima, in carica p90). Non è un intervallo calibrato: vedi docs/MODEL_CARD.md.
            "crossing": {
                "status": cross[1].status,
                "central_utc": cross[1].at,
                "early_utc": cross[0].at,
                "late_utc": cross[2].at,
            },
            # Ora in cui di solito si ricarica (secondo il modello) e, da lì, curva estrapolata al ritmo dato.
            "habitual_recharge_utc": None if plug is None or charge else float(times[plug]),
            "habitual_unplug_utc": None if plug is None or not charge else float(times[plug]),
            "extrapolated_from_utc": None if rate is None else float(times[plug]),
            "extrapolation_rate_pct_per_hour": None if rate is None else round(rate, 2),
            "curve": [{"t": float(t), "p10": round(float(a), 2), "p50": round(float(b), 2), "p90": round(float(c), 2)}
                      for t, (a, b, c) in zip(times, q)],
            "elapsed_ms": round((time.perf_counter() - t0) * 1000),
        }
