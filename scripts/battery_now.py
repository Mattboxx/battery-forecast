"""Previsione della batteria per l'app Autonomia.

  .venv/bin/python scripts/battery_now.py --level 73 --ac 0     una previsione, JSON su stdout
  .venv/bin/python scripts/battery_now.py --serve               servizio: legge righe JSON su stdin
                                                                ({"id":1,"level":73,"ac":0}) e risponde
                                                                con una riga JSON per richiesta

Il servizio tiene il modello in memoria: l'avvio costa ~2 s di CPU (PyTorch), una previsione
già avviata meno di mezzo secondo. L'app lo usa in questo modo.

Storia usata, unita in una serie a 5 minuti:
  - database energetico di macOS (~8 giorni, sempre su copie)
  - registro scritto dall'app in ~/Library/Application Support/Autonomia/battery_log.csv,
    che cresce nel tempo: più settimane ci sono, più il modello conosce le abitudini
Scenario: da adesso il Mac non viene attaccato alla corrente.
"""
import argparse, gc, gzip, json, os, select, shutil, sqlite3, sys, tempfile, time
from pathlib import Path

os.environ.setdefault("HF_HUB_OFFLINE", "1")   # il modello è già in cache: niente rete a ogni giro
# Nell'app scaricata il modello sta nel bundle (cartella con config.json + model.safetensors)
MODEL = os.environ.get("AUTONOMIA_MODEL", "mistralai/Timestral-28M")
import numpy as np
import pandas as pd
from timestral import TimestralForecaster

SRC = Path("/private/var/db/powerlog/Library/BatteryLife")
SUPPORT = Path.home() / "Library/Application Support/Autonomia"
CACHE = Path.home() / "Library/Caches/Autonomia"
STEP = "5min"
HORIZON = 24 * 12                 # 24 ore: oltre non è verificato
THRESHOLD = 5                     # "scarica" = sotto il 5%
POWERLOG_TTL = 20 * 60            # il database di macOS si rilegge al massimo ogni 20 minuti
QUERY = "select timestamp as ts, Level as level, ExternalConnected as ac from PLBatteryAgent_EventBackward_Battery"


def read_db(path: Path) -> pd.DataFrame:
    con = sqlite3.connect(path)
    try:
        return pd.read_sql(QUERY, con)
    except Exception:
        return pd.DataFrame(columns=["ts", "level", "ac"])
    finally:
        con.close()


def read_powerlog() -> pd.DataFrame:
    CACHE.mkdir(parents=True, exist_ok=True)
    frames, keep = [], set()
    for gz in sorted((SRC / "Archives").glob("*.PLSQL.gz")):
        cached = CACHE / gz.name.replace(".PLSQL.gz", ".csv")
        keep.add(cached.name)
        if not cached.exists():                       # gli archivi non cambiano: si estraggono una volta
            with tempfile.TemporaryDirectory() as tmp:
                db = Path(tmp) / "archive.PLSQL"
                with gzip.open(gz) as fi, open(db, "wb") as fo:
                    shutil.copyfileobj(fi, fo)
                read_db(db).to_csv(cached, index=False)
        frames.append(pd.read_csv(cached))
    for old in CACHE.glob("powerlog_*.csv"):          # macOS ha cancellato l'archivio: via anche la cache
        if old.name not in keep:
            old.unlink()
    with tempfile.TemporaryDirectory() as tmp:        # db corrente + wal, così entrano i dati recenti
        for f in SRC.glob("CurrentPowerlog.PLSQL*"):
            shutil.copy(f, Path(tmp) / f.name)
        frames.append(read_db(Path(tmp) / "CurrentPowerlog.PLSQL"))
    return pd.concat(frames)


def read_own_log() -> pd.DataFrame:
    path = SUPPORT / "battery_log.csv"
    if not path.exists():
        return pd.DataFrame(columns=["ts", "level", "ac"])
    return pd.read_csv(path)


def crossing(curve: np.ndarray, times: pd.DatetimeIndex):
    idx = np.flatnonzero(curve <= THRESHOLD)
    return float(times[idx[0]].timestamp()) if len(idx) else None


class Predictor:
    def __init__(self):
        try:
            self.fc = TimestralForecaster.from_pretrained(MODEL, local_files_only=True)
        except Exception:
            self.fc = TimestralForecaster.from_pretrained(MODEL)
        lv = np.array(self.fc.quantile_levels)
        self.p10, self.p50, self.p90 = int(np.argmin(abs(lv - .1))), self.fc.median_index, int(np.argmin(abs(lv - .9)))
        self._powerlog, self._powerlog_at = None, 0.0
        gc.collect()

    def powerlog(self) -> pd.DataFrame:
        if self._powerlog is None or time.time() - self._powerlog_at > POWERLOG_TTL:
            self._powerlog, self._powerlog_at = read_powerlog(), time.time()
        return self._powerlog

    def series(self, level_now: float, ac_now: int) -> pd.DataFrame:
        df = pd.concat([self.powerlog(), read_own_log(),
                        pd.DataFrame([{"ts": time.time(), "level": level_now, "ac": ac_now}])])
        df = df.astype({"ts": float, "level": float, "ac": float}).dropna(subset=["ts", "level"])
        df["ts"] = pd.to_datetime(df["ts"], unit="s", utc=True)
        g = df.set_index("ts").sort_index().resample(STEP).agg({"level": "mean", "ac": "last"})
        # durante lo stop il livello cambia poco e in modo regolare: si interpola fino a 24 ore di buco
        g["level"] = g["level"].interpolate(limit=24 * 12, limit_area="inside")
        g["ac"] = g["ac"].ffill().fillna(0)
        return g

    def predict(self, level: float, ac: int) -> dict:
        t0 = time.perf_counter()
        g = self.series(level, ac)
        lvl, acs = g["level"].values.astype(np.float32), g["ac"].values.astype(np.float32)
        known = np.concatenate([acs, np.zeros(HORIZON, np.float32)])
        q = self.fc.forecast(lvl[None, None], prediction_length=HORIZON,
                             known_covariates=known[None, None]).quantiles[0, 0].numpy()
        # Senza caricatore la carica può solo scendere. Se ogni percorso scende, scende anche ogni
        # quantile: il minimo cumulato rende la previsione coerente con la fisica.
        q = np.minimum.accumulate(np.minimum(q, level), axis=0)
        times = pd.date_range(g.index[-1] + pd.Timedelta(STEP), periods=HORIZON, freq=STEP)
        observed = g["level"].notna()
        own = read_own_log()
        return {
            "generated_at": time.time(),
            "level": level,
            "on_ac": bool(ac),
            "threshold": THRESHOLD,
            "horizon_hours": HORIZON // 12,
            "history_days": round((g.index[observed][-1] - g.index[observed][0]).total_seconds() / 86400, 1),
            "own_log_days": round(len(own) * 5 / 1440, 1),
            "empty_at": crossing(q[:, self.p50], times),
            "empty_early": crossing(q[:, self.p10], times),
            "empty_late": crossing(q[:, self.p90], times),
            "curve": [{"t": float(t.timestamp()), "p10": round(float(a), 1), "p50": round(float(b), 1),
                       "p90": round(float(c), 1)}
                      for t, a, b, c in zip(times[2::3], q[2::3, self.p10], q[2::3, self.p50], q[2::3, self.p90])],
            "elapsed_ms": round((time.perf_counter() - t0) * 1000),
        }


def read_lines():
    """Righe di stdin. Finisce se stdin si chiude o se l'app è sparita (il padre diventa launchd, pid 1):
    senza questo un'app chiusa con SIGTERM lascerebbe il servizio vivo con i suoi ~310 MB."""
    buf = b""
    while True:
        ready, _, _ = select.select([0], [], [], 5.0)
        if not ready:
            if os.getppid() == 1:
                return
            continue
        chunk = os.read(0, 65536)
        if not chunk:
            return
        buf += chunk
        while b"\n" in buf:
            line, buf = buf.split(b"\n", 1)
            yield line.decode()


def serve():
    t0 = time.perf_counter()
    p = Predictor()
    print(json.dumps({"ready": True, "startup_ms": round((time.perf_counter() - t0) * 1000)}), flush=True)
    for line in read_lines():
        line = line.strip()
        if not line:
            continue
        req = {}
        try:
            req = json.loads(line)
            out = p.predict(float(req["level"]), int(req.get("ac", 0)))
        except Exception as e:
            out = {"error": f"{type(e).__name__}: {e}"}
        out["id"] = req.get("id") if isinstance(req, dict) else None
        print(json.dumps(out), flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--level", type=float)
    ap.add_argument("--ac", type=int, default=0)
    ap.add_argument("--serve", action="store_true")
    args = ap.parse_args()
    if args.serve:
        serve()
    elif args.level is None:
        ap.error("serve --level oppure --serve")
    else:
        print(json.dumps(Predictor().predict(args.level, args.ac)))


if __name__ == "__main__":
    main()
