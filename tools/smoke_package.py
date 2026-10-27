"""Exercise the frozen engine and verify the offline legal inventory, without installing locally."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import struct
import subprocess
import time


def pe_arch(path):
    with path.open("rb") as f:
        f.seek(0x3C)
        offset = struct.unpack("<I", f.read(4))[0]
        f.seek(offset)
        if f.read(4) != b"PE\0\0":
            raise ValueError(f"Invalid executable: {path.name}")
        return struct.unpack("<H", f.read(2))[0]


def run(root, arch):
    root = root.resolve()
    expected = {"x64":0x8664,"arm64":0xAA64}[arch]
    for name in ["BatteryForecast.exe", "engine/battery_forecast.exe"]:
        if pe_arch(root/name) != expected:
            raise ValueError(f"Wrong architecture for {name}")
    spec = importlib.util.spec_from_file_location("legal", Path(__file__).with_name("licenses.py"))
    legal = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(legal)
    legal.verify_bundle(root/"licenses")
    model = root/"engine/model"
    info = json.loads((model/"MODEL_INFO.json").read_text(encoding="utf-8"))
    for name in ["config.json", "model.safetensors", "LICENSE"]:
        if legal.sha(model/name) != info["files"][name]:
            raise ValueError(f"Model file changed: {name}")
    now = 1800000000
    samples = []
    for day in range(7,0,-1):
        for minute in range(0,1440,5):
            h = minute/60
            level = 100-8*(h-9) if 9<=h<15 else 100 if h<9 else min(100,52+30*(h-15))
            samples.append([now-day*86400+minute*60,level,0 if 9<=h<15 else 1])
    request = {"type":"forecast","protocol_version":1,"id":42,"now_utc":now,"level":60,"ac":0,
               "reserve_pct":5,"samples":samples}
    # This catches an old frozen worker accidentally shipped beside the new app: merely ignoring
    # the additive protocol fields would count the filled half-hour as a continuous session.
    preparation_request = {"type":"forecast","protocol_version":1,"id":43,"now_utc":now,"level":74,"ac":0,
        "reserve_pct":5,"samples":[[now-1800,80,0,True],[now-1200,78,0,True],
        [now-900,77,0,False],[now-600,76,0,True]],"discontinuities_utc":[now-900]}
    env = {**os.environ,"HF_HUB_OFFLINE":"1","TRANSFORMERS_OFFLINE":"1","PYTHONNOUSERSITE":"1"}
    env.pop("PYTHONPATH",None)
    started = time.monotonic()
    result = subprocess.run([str(root/"engine/battery_forecast.exe"),"--model",str(model),"--threads","2"],
        input=json.dumps(request)+"\n"+json.dumps(preparation_request)+"\n",capture_output=True,text=True,encoding="utf-8",env=env,cwd=root,timeout=120)
    messages = [json.loads(line) for line in result.stdout.splitlines() if line.strip()]
    assert result.returncode == 0, result.stderr[-3000:]
    assert messages[0]["type"] == "ready", messages
    forecast = next(x for x in messages if x["type"] == "forecast" and x["id"]==42)
    preparation = next(x for x in messages if x["type"] == "forecast" and x["id"]==43)
    assert preparation["quality"]["discharge_sessions"]==0, preparation["quality"]
    assert preparation["quality"]["observed_hours"]==0, preparation["quality"]
    assert len(preparation["curve"])==288
    assert forecast["id"] == 42 and len(forecast["curve"]) == 288
    assert forecast["model_revision"] == info["revision"]
    previous = 60.0
    for point in forecast["curve"]:
        assert 0 <= point["p10"] <= point["p50"] <= point["p90"] <= 60
        assert point["p50"] <= previous + 1e-8
        previous = point["p50"]
    report = {"architecture":arch,"engine":"frozen executable","model_revision":info["revision"],
              "synthetic_forecast_points":288,"legal_inventory_verified":True,"preprocessing_regressions_verified":True,
              "offline_model_flags":True,"network_adapter_disabled":False,
              "elapsed_seconds":round(time.monotonic()-started,2)}
    print(json.dumps(report,indent=2))
    return report


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("root",type=Path)
    parser.add_argument("--arch",choices=["x64","arm64"],default="x64")
    parser.add_argument("--report",type=Path)
    args=parser.parse_args()
    report=run(args.root,args.arch)
    if args.report:
        args.report.write_text(json.dumps(report,indent=2)+"\n",encoding="utf-8")
