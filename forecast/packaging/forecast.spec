# PyInstaller, modalità cartella: runtime Python, PyTorch CPU e Timestral senza estrazione a ogni avvio.
#   forecast\.venv\Scripts\python -m PyInstaller --noconfirm --distpath forecast\dist --workpath forecast\build forecast\packaging\forecast.spec
# I pesi del modello NON sono qui: tools/package.ps1 li copia accanto in engine\model.
from pathlib import Path

from PyInstaller.utils.hooks import collect_submodules

root = Path(SPECPATH).parent

a = Analysis(
    [str(root / "packaging" / "entry.py")],
    pathex=[str(root / "src")],
    hiddenimports=["battery_forecast.engine", *collect_submodules("timestral")],
    # Nulla di ciò che segue serve all'inferenza: si esclude per non distribuirlo.
    excludes=["tkinter", "matplotlib", "pandas", "IPython", "pytest", "PyInstaller", "setuptools", "pip",
              "torchvision", "torchaudio", "tensorboard"],
    noarchive=False,
)
# Windows 10+ supplies UCRT and API-set contracts. Do not redistribute SDK/System32 copies.
a.binaries = [row for row in a.binaries if Path(row[0]).name.lower() != "ucrtbase.dll"
              and not Path(row[0]).name.lower().startswith("api-ms-win-")]
pyz = PYZ(a.pure)
exe = EXE(
    pyz,
    a.scripts,
    [],
    exclude_binaries=True,
    name="battery_forecast",
    console=True,            # stdin/stdout per il protocollo; l'app lo avvia senza finestra (CreateNoWindow)
    upx=False,
    icon=str(root.parent / "windows" / "assets" / "app.ico"),
)
coll = COLLECT(exe, a.binaries, a.datas, name="engine", upx=False)
