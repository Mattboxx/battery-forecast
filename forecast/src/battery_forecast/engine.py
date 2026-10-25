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


def observed_rate(s: series.Series, steps: int = 12) -> float | None:
    """%/h di scarica nell'ultima ora osservata a batteria, se ci sono abbastanza passi."""
    lv, ac = s.level[-steps:], s.ac[-steps:]
    ok = np.flatnonzero(np.isfinite(lv) & (ac == 0))
    if len(ok) < steps // 2 or ok[-1] == ok[0]:
        return None
    return float((lv[ok[0]] - lv[ok[-1]]) / ((ok[-1] - ok[0]) * STEP_SECONDS / 3600))


def quality_state(s: series.Series) -> str:
    if s.observed_days * 24 < MIN_HOURS_USABLE or s.discharge_sessions == 0:
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

    def predict(self, req: dict) -> dict:
        t0 = time.perf_counter()
        s = series.build(req["samples"], req["now"], req["level"], req["ac"])
        h = req["horizon"]
        # Scenario "no_charge": da ora in poi l'alimentatore resta scollegato.
        known = np.concatenate([s.ac, np.zeros(h, np.float32)])
        past, categories, context_features = series.past_covariates(req.get("past_covariates", {}), s)
        with self._torch.inference_mode():
            q = self.fc.forecast(s.level[None, None], prediction_length=h,
                                 known_covariates=known[None, None], past_covariates=past,
                                 categorical_past_covariates=categories).quantiles[0, 0].numpy()
        times = s.end + STEP_SECONDS * np.arange(1, h + 1, dtype=np.float64)
        q, plug, rate = self._no_charge(q[:, self.qi], req["level"], s)
        cross = [physics.crossing(q[:, k], times, req["reserve"], req["level"], req["now"]) for k in range(3)]
        return {
            "type": "forecast",
            "id": req["id"],
            "generated_at_utc": time.time(),
            "context_end_utc": req["now"],
            "source": "timestral",
            "model_id": self.model_id,
            "model_revision": self.model_revision,
            "scenario": req["scenario"],
            "reserve_pct": req["reserve"],
            "level": req["level"],
            "context_features": context_features,
            "quality": {
                "state": quality_state(s),
                "observed_days": s.observed_days,
                "coverage": s.coverage,
                "discharge_sessions": s.discharge_sessions,
            },
            # p50 = stima centrale; p10 prudente (prima), p90 ottimista (dopo). Non è un
            # intervallo calibrato del tempo di autonomia: vedi docs/MODEL_CARD.md.
            "crossing": {
                "status": cross[1].status,
                "central_utc": cross[1].at,
                "early_utc": cross[0].at,
                "late_utc": cross[2].at,
            },
            # Ora in cui di solito si ricarica (secondo il modello) e, da lì, curva estrapolata al ritmo dato.
            "habitual_recharge_utc": None if plug is None else float(times[plug]),
            "extrapolated_from_utc": None if rate is None else float(times[plug]),
            "extrapolation_rate_pct_per_hour": None if rate is None else round(rate, 2),
            "curve": [{"t": float(t), "p10": round(float(a), 2), "p50": round(float(b), 2), "p90": round(float(c), 2)}
                      for t, (a, b, c) in zip(times, q)],
            "elapsed_ms": round((time.perf_counter() - t0) * 1000),
        }
