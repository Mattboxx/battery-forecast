"""Vincoli fisici sulla curva prevista e attraversamento della soglia (riserva in scarica, obiettivo in carica)."""
from __future__ import annotations

from dataclasses import dataclass

import numpy as np


def constrain_no_charge(q: np.ndarray, level_now: float) -> np.ndarray:
    """Scenario senza ricarica: la carica resta in 0–100, non supera quella attuale e non risale.

    `q` ha forma (passi, quantili) con i quantili in ordine crescente di livello. Il minimo cumulato
    rende ogni quantile non crescente nel tempo; l'ordinamento per riga ripristina p10 ≤ p50 ≤ p90
    se il modello li avesse incrociati. Si applica solo al futuro, mai ai dati osservati.
    """
    q = np.clip(np.asarray(q, dtype=np.float64), 0.0, 100.0)
    q = np.minimum(q, level_now)
    q = np.minimum.accumulate(q, axis=0)
    return np.sort(q, axis=1)


RISE_PCT = 2.0           # risalita della mediana grezza che indica una ricarica abituale
RISE_CONFIRM_STEPS = 2   # deve durare almeno 10 minuti: non un singolo passo rumoroso
MIN_RATE = 0.5           # %/h: sotto, il ritmo non basta per estrapolare


def habitual_recharge(raw_p50: np.ndarray, level_now: float) -> int | None:
    """Primo passo in cui la mediana grezza risale sopra il suo minimo: il modello prevede una ricarica.

    Timestral prevede il livello secondo le abitudini, ricariche comprese. Nello scenario "senza ricarica"
    il vincolo fisico renderebbe piatta quella risalita, cioè inventerebbe un consumo nullo: oltre questo
    passo la previsione sulle abitudini non descrive più lo scenario.
    """
    low, run = level_now, 0
    for i, v in enumerate(raw_p50):
        if v - low >= RISE_PCT:
            run += 1
            if run >= RISE_CONFIRM_STEPS:
                return i - RISE_CONFIRM_STEPS + 1
        else:
            run = 0
            low = min(low, v)
    return None


def extend_after(q: np.ndarray, start: int, rate_per_step: float, base: np.ndarray, spread: float = 0.25) -> np.ndarray:
    """Dal passo `start` ogni quantile scende da `base` al ritmo dato (±spread per prudente/ottimista).

    Euristica dichiarata (docs/MODEL_CARD.md): oltre la ricarica abituale le abitudini non dicono quanto
    si consumerebbe senza alimentatore, quindi si prosegue al ritmo medio previsto fin lì.
    """
    q = np.asarray(q, dtype=np.float64).copy()
    k = np.arange(1, len(q) - start + 1)[:, None]
    rates = rate_per_step * np.array([1 + spread, 1.0, 1 - spread])[None, :]
    q[start:] = np.asarray(base, dtype=np.float64)[None, :] - k * rates
    return q


def constrain_charge(q: np.ndarray, level_now: float) -> np.ndarray:
    """Scenario "resto collegato": in 0–100, mai sotto il livello attuale, mai in discesa, quantili ordinati."""
    q = np.clip(np.asarray(q, dtype=np.float64), 0.0, 100.0)
    q = np.maximum(q, level_now)
    q = np.maximum.accumulate(q, axis=0)
    return np.sort(q, axis=1)


def habitual_unplug(raw_p50: np.ndarray, level_now: float) -> int | None:
    """Primo passo in cui la mediana grezza scende sotto il suo massimo: il modello prevede che di solito
    a quel punto si stacchi l'alimentatore. Nello scenario "resto collegato" il vincolo renderebbe piatta
    la discesa, cioè una carica che si ferma senza motivo: oltre, le abitudini non descrivono lo scenario."""
    high, run = level_now, 0
    for i, v in enumerate(raw_p50):
        if high - v >= RISE_PCT:
            run += 1
            if run >= RISE_CONFIRM_STEPS:
                return i - RISE_CONFIRM_STEPS + 1
        else:
            run = 0
            high = max(high, v)
    return None


def extend_charge_after(q: np.ndarray, start: int, rate_per_step: float, base: np.ndarray, spread: float = 0.25) -> np.ndarray:
    """Dal passo `start` ogni quantile si avvicina al 100% partendo da `base` con pendenza iniziale `rate`
    (±spread: p10 più lento, p90 più veloce) e rallenta in modo esponenziale vicino al pieno, come nella
    fase finale a tensione costante. Euristica dichiarata, come extend_after per la scarica."""
    q = np.asarray(q, dtype=np.float64).copy()
    k = np.arange(1, len(q) - start + 1, dtype=np.float64)
    for j, mult in enumerate((1 - spread, 1.0, 1 + spread)):
        gap = max(0.0, 100.0 - float(base[j]))
        if gap <= 0:
            q[start:, j] = base[j]
            continue
        q[start:, j] = 100.0 - gap * np.exp(-rate_per_step * mult * k / gap)
    return q


@dataclass(frozen=True)
class Crossing:
    status: str                 # "already_below" | "already_reached" | "within_horizon" | "beyond_horizon"
    at: float | None            # secondi UTC, arrotondati al minuto


def crossing(curve: np.ndarray, times: np.ndarray, reserve: float, level_now: float, now: float) -> Crossing:
    """Primo istante in cui `curve` scende alla riserva.

    Si interpola linearmente dentro il passo attraversato (partendo dal livello attuale per il primo),
    poi si arrotonda al minuto: una precisione al secondo sarebbe finta.
    """
    if level_now <= reserve:
        return Crossing("already_below", None)
    idx = np.flatnonzero(curve <= reserve)
    if not len(idx):
        return Crossing("beyond_horizon", None)
    i = int(idx[0])
    t1, v1 = float(times[i]), float(curve[i])
    t0, v0 = (now, level_now) if i == 0 else (float(times[i - 1]), float(curve[i - 1]))
    at = t1 if v0 == v1 else t0 + (v0 - reserve) / (v0 - v1) * (t1 - t0)
    return Crossing("within_horizon", round(at / 60) * 60.0)


def crossing_up(curve: np.ndarray, times: np.ndarray, target: float, level_now: float, now: float) -> Crossing:
    """Primo istante in cui `curve` sale all'obiettivo di carica (interpolato nel passo, arrotondato al minuto)."""
    if level_now >= target:
        return Crossing("already_reached", None)
    idx = np.flatnonzero(curve >= target)
    if not len(idx):
        return Crossing("beyond_horizon", None)
    i = int(idx[0])
    t1, v1 = float(times[i]), float(curve[i])
    t0, v0 = (now, level_now) if i == 0 else (float(times[i - 1]), float(curve[i - 1]))
    at = t1 if v0 == v1 else t0 + (target - v0) / (v1 - v0) * (t1 - t0)
    return Crossing("within_horizon", round(at / 60) * 60.0)
