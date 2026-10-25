"""Raccoglie le licenze di ciò che il pacchetto distribuisce, nella cartella licenses/ dell'app.

  forecast\\.venv\\Scripts\\python tools\\licenses.py <cartella-licenses> [<engine\\_internal>]

Copia i testi originali (mai riassunti): Battery Forecast, Timestral (codice e pesi), Python, ogni pacchetto
Python incluso nel motore e i componenti .NET. Scrive THIRD-PARTY-NOTICES.txt con nome, versione e licenza.
Le licenze di ogni pacchetto Python vanno in un archivio zip con i percorsi originali: PyTorch ne ha
centinaia, annidate, che sfiorerebbero il limite di 260 caratteri dei percorsi Windows. Con il secondo
argomento le stesse cartelle annidate si tolgono dai dist-info del motore impacchettato.
"""
import shutil
import sys
import zipfile
from importlib import metadata
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
# strumenti di sviluppo: non finiscono nel motore impacchettato
DEV = {"pip", "setuptools", "pyinstaller", "pyinstaller-hooks-contrib", "altgraph", "pefile", "pywin32-ctypes",
       "packaging", "pytest", "iniconfig", "pluggy", "pygments", "colorama"}

DOTNET = [
    (".NET runtime, WPF, Windows Forms", "MIT", "https://github.com/dotnet/runtime/blob/main/LICENSE.TXT"),
    ("Microsoft.Data.Sqlite", "MIT", "https://github.com/dotnet/efcore/blob/main/LICENSE.txt"),
    ("SQLitePCLRaw", "Apache-2.0", "https://github.com/ericsink/SQLitePCL.raw/blob/master/LICENSE.TXT"),
    ("SQLite", "Public domain", "https://sqlite.org/copyright.html"),
]


def license_files(dist):
    for f in dist.files or []:
        name = f.name.upper()
        if any(k in name for k in ("LICENSE", "LICENCE", "COPYING", "NOTICE")) and not name.endswith((".PY", ".PYC")):
            yield Path(dist.locate_file(f))


def main(out: Path):
    out.mkdir(parents=True, exist_ok=True)
    lic = ROOT / "licenses"
    shutil.copy(ROOT / "LICENSE", out / "LICENSE-MIT.txt")
    for f in lic.glob("*.txt"):
        shutil.copy(f, out / f.name)
    model_license = ROOT / "forecast" / "models" / "Timestral-28M" / "LICENSE"
    if model_license.exists():
        shutil.copy(model_license, out / "Timestral-28M-weights-LICENSE.txt")
    py_license = Path(sys.base_prefix) / "LICENSE.txt"
    if py_license.exists():
        shutil.copy(py_license, out / "Python-LICENSE.txt")

    lines = ["Battery Forecast — third-party components", "",
             "Python packages bundled in engine\\ (license texts in python\\<package>-<version>-licenses.zip):", ""]
    for dist in sorted(metadata.distributions(), key=lambda d: d.metadata["Name"].lower()):
        name = dist.metadata["Name"]
        if name.lower() in DEV:
            continue
        kind = dist.metadata.get("License-Expression") or dist.metadata.get("License") or ""
        kind = kind.splitlines()[0][:80] if kind else "see license files"
        lines.append(f"- {name} {dist.version}: {kind}")
        files = [f for f in license_files(dist) if f.is_file()]
        if files:
            (out / "python").mkdir(exist_ok=True)
            base = Path(dist.locate_file(""))
            with zipfile.ZipFile(out / "python" / f"{name}-{dist.version}-licenses.zip", "w", zipfile.ZIP_DEFLATED) as z:
                for f in files:
                    z.write(f, f.relative_to(base).as_posix())
    lines += ["", "Python " + sys.version.split()[0] + ": PSF License (Python-LICENSE.txt)", "",
              ".NET components in the app folder:", ""]
    lines += [f"- {n}: {k} — {u}" for n, k, u in DOTNET]
    lines += ["", "Timestral code: Apache-2.0 (Timestral-codice-Apache-2.0.txt).",
              "Timestral weights: modified MIT license, exact text in Timestral-28M-weights-LICENSE.txt.",
              "Battery Forecast and Till Dusk (original macOS app by Matteo Bonfanti): MIT (LICENSE-MIT.txt)."]
    (out / "THIRD-PARTY-NOTICES.txt").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"licenze in {out}")


def strip_nested(internal: Path):
    """Toglie le sottocartelle licenses/ dei dist-info impacchettati (già copiate negli zip)."""
    for d in internal.glob("*.dist-info/licenses"):
        shutil.rmtree(d)


if __name__ == "__main__":
    main(Path(sys.argv[1]))
    if len(sys.argv) > 2:
        strip_nested(Path(sys.argv[2]))
