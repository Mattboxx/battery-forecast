"""Protocollo JSON Lines tra l'app e il motore: una richiesta e una risposta per riga, UTF-8.

Stdout porta solo messaggi del protocollo; la diagnostica va su stderr. NaN e infiniti non sono JSON
valido: diventano null. Esempi condivisi con i test C# in forecast/tests/fixtures/.
"""
from __future__ import annotations

import json
import math

from .series import CONTEXT_RANGES,MAX_CONTEXT_CHANNELS,context_range

from . import DEFAULT_RESERVE, HORIZON_STEPS, PROTOCOL_VERSION, STEP_SECONDS


class ProtocolError(Exception):
    def __init__(self, code: str, message: str, retryable: bool = False):
        super().__init__(message)
        self.code, self.retryable = code, retryable


def _clean(x):
    if isinstance(x, float) and not math.isfinite(x):
        return None
    if isinstance(x, dict):
        return {k: _clean(v) for k, v in x.items()}
    if isinstance(x, (list, tuple)):
        return [_clean(v) for v in x]
    return x


def encode(msg: dict) -> bytes:
    msg = {"protocol_version": PROTOCOL_VERSION, **msg}
    return json.dumps(_clean(msg), ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode() + b"\n"


def error(code: str, message: str, id=None, retryable: bool = False) -> bytes:
    return encode({"type": "error", "id": id, "code": code, "message": message, "retryable": retryable})


def request_id(line: bytes):
    """L'id di una richiesta anche quando non è valida, così l'app sa a quale richiesta va l'errore."""
    try:
        rid = json.loads(line).get("id")
    except (ValueError, UnicodeDecodeError, AttributeError):
        return None
    return rid if isinstance(rid, int) and not isinstance(rid, bool) else None


def parse_request(line: bytes) -> dict:
    """Valida una richiesta `forecast` e restituisce i campi con i valori predefiniti."""
    try:
        req = json.loads(line)
    except (ValueError, UnicodeDecodeError) as e:
        raise ProtocolError("bad_json", f"JSON non valido: {e}") from None
    if not isinstance(req, dict):
        raise ProtocolError("bad_request", "la richiesta deve essere un oggetto")
    if req.get("protocol_version") != PROTOCOL_VERSION:
        raise ProtocolError("bad_version", f"protocollo {req.get('protocol_version')!r}, atteso {PROTOCOL_VERSION}")
    kind = req.get("type")
    if kind == "ping":
        return {"type": "ping", "id": req.get("id")}
    if kind != "forecast":
        raise ProtocolError("bad_request", f"tipo di richiesta sconosciuto: {kind!r}")
    try:
        out = {
            "type": "forecast",
            "id": req["id"],
            "now": float(req["now_utc"]),
            "level": float(req["level"]),
            "ac": None if req.get("ac") is None else int(req["ac"]),
            "reserve": float(req.get("reserve_pct", DEFAULT_RESERVE)),
            "horizon": int(req.get("horizon_steps", HORIZON_STEPS)),
            "scenario": req.get("scenario", "no_charge"),
            # l'app manda target_pct: null nello scenario di scarica
            "target": float(80 if req.get("target_pct") is None else req["target_pct"]),
            "samples": [(s[0], s[1], s[2] if len(s) > 2 else None, s[3] if len(s)>3 else True) for s in req.get("samples", [])],
        }
    except (KeyError, TypeError, ValueError, IndexError) as e:
        raise ProtocolError("bad_request", f"campo mancante o non valido: {e}") from None
    if int(req.get("step_seconds", STEP_SECONDS)) != STEP_SECONDS:
        raise ProtocolError("bad_request", f"step_seconds deve essere {STEP_SECONDS}")
    if not math.isfinite(out["now"]) or any(not isinstance(s[3],bool) for s in out["samples"]):
        raise ProtocolError("bad_request", "invalid origin or observation flag")
    boundaries=req.get("discontinuities_utc",[])
    if not isinstance(boundaries,list) or len(boundaries)>40000:
        raise ProtocolError("bad_request", "invalid discontinuities_utc")
    try:
        if any(isinstance(t,bool) or not isinstance(t,(float,int)) or not math.isfinite(t) for t in boundaries):
            raise ValueError("expected finite UTC timestamps")
        out["discontinuities"]=sorted({float(t) for t in boundaries if t<=out["now"]})
    except (TypeError,ValueError,OverflowError) as e:
        raise ProtocolError("bad_request", f"invalid discontinuities_utc: {e}") from None
    if not 0 <= out["level"] <= 100 or not 0 <= out["reserve"] < 100:
        raise ProtocolError("bad_request", "livello o riserva fuori scala")
    if not 1 <= out["horizon"] <= HORIZON_STEPS:
        raise ProtocolError("bad_request", f"horizon_steps deve stare fra 1 e {HORIZON_STEPS}")
    if out["scenario"] not in ("no_charge", "charge"):
        raise ProtocolError("bad_request", f"scenario non supportato: {out['scenario']!r}")
    if out["scenario"] == "charge" and not 50 <= out["target"] <= 100:
        raise ProtocolError("bad_request", "target_pct deve stare fra 50 e 100")
    context = req.get("past_covariates", {})
    if not isinstance(context, dict) or len(context) > MAX_CONTEXT_CHANNELS:
        raise ProtocolError("bad_request", "past_covariates must contain supported named historical channels")
    parsed = {}
    try:
        for name, pairs in context.items():
            if not isinstance(name,str) or context_range(name) is None or not isinstance(pairs, list) or len(pairs) > 40000:
                raise ValueError("unknown or oversized historical channel")
            clean = []
            for pair in pairs:
                if not isinstance(pair, (list, tuple)) or len(pair) != 2:
                    raise ValueError("expected [utc_seconds, value_or_null]")
                timestamp = float(pair[0])
                value = None if pair[1] is None else float(pair[1])
                if not math.isfinite(timestamp) or (value is not None and not math.isfinite(value)):
                    raise ValueError("nonfinite historical observation")
                if timestamp <= out["now"]:
                    clean.append((timestamp, value))
            parsed[name] = clean
    except (ValueError, TypeError) as e:
        raise ProtocolError("bad_request", f"invalid past_covariates: {e}") from None
    out["past_covariates"] = parsed
    return out
