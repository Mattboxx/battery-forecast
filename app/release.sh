#!/bin/bash
# Pacchetto da scaricare: l'app (nome in app.conf) con dentro Python 3.12, le dipendenze del servizio, lo script
# e il modello Timestral-28M (Contents/Resources/runtime), firmata ad-hoc, in dist/<nome senza spazi>-<versione>.zip.
# Niente dati personali: entrano solo battery_now.py, il modello e le licenze.
set -euo pipefail
cd "$(dirname "$0")"
source ./app.conf
PROJECT="$(cd .. && pwd)"
TIMESTRAL="timestral @ git+https://github.com/mistralai/timestral.git@a534bdbd244d64f3d9d743f46c7bf6fc330ed4ff"

./build.sh --no-install
APP=".build/bundle/$NAME.app"
RT="$APP/Contents/Resources/runtime"

# --- Python portabile con le sole dipendenze del servizio (si prepara una volta, poi si riusa) ---
PY=".build/pyruntime"
if [ ! -x "$PY/bin/python3" ]; then
    echo "▸ Python 3.12 portabile + timestral, torch, numpy, pandas…"
    uv python install 3.12 >/dev/null
    SRC="$(cd "$(dirname "$(readlink -f "$(uv python find --managed-python --no-project 3.12)")")/.." && pwd)"
    rm -rf "$PY" && cp -R "$SRC" "$PY"
    rm -f "$PY/lib/python3.12/EXTERNALLY-MANAGED"
    (cd "$PROJECT" && uv export --frozen --no-hashes --no-emit-project) > .build/constraints.txt  # stesse versioni dei test
    uv pip install -q --python "$PY/bin/python3" -c .build/constraints.txt "$TIMESTRAL" pandas
    SP="$PY/lib/python3.12/site-packages"
    # Peso morto: intestazioni C, protoc, test, Tcl/Tk, pip, e sympy/networkx (servono solo a
    # torch.compile, che il servizio non usa: provato bloccandoli). Le licenze (*.dist-info) restano.
    rm -rf "$PY"/include "$PY"/share "$PY"/lib/{tcl9,tcl9.0,tk9.0,itcl*,thread*,libtcl*,pkgconfig} \
        "$PY"/lib/python3.12/{test,idlelib,tkinter,turtledemo,ensurepip,lib2to3,config-3.12-darwin} \
        "$PY"/lib/python3.12/lib-dynload/_tkinter* \
        "$SP"/torch/{include,share} "$SP"/torch/bin/protoc* "$SP"/pip "$SP"/pip-*.dist-info \
        "$SP"/{sympy,mpmath,networkx,isympy.py} "$SP"/{sympy,mpmath,networkx}-*.dist-info
    find "$SP/numpy" "$SP/pandas" -type d -name tests -prune -exec rm -rf {} +
    find "$PY" -name __pycache__ -prune -exec rm -rf {} +
    # .pyc pronti: l'app è firmata e non ci si può scrivere (PYTHONDONTWRITEBYTECODE in Forecast.swift)
    "$PY/bin/python3" -m compileall -q -j0 "$PY/lib" >/dev/null || true
fi

echo "▸ runtime, modello e licenze nel bundle…"
mkdir -p "$RT/model"
cp -R "$PY" "$RT/python"
cp "$PROJECT/scripts/battery_now.py" "$RT/battery_now.py"
SNAP="$(ls -d "$HOME"/.cache/huggingface/hub/models--mistralai--Timestral-28M/snapshots/* | head -1)"
cp -L "$SNAP/config.json" "$SNAP/model.safetensors" "$RT/model/"
cp -R licenses "$APP/Contents/Resources/licenses"

# --- Firma ad-hoc: prima ogni libreria ed eseguibile, poi l'app ---
echo "▸ firma ad-hoc…"
# Si firma una copia fuori da ~/Documents: iCloud Drive rimette attributi estesi che codesign rifiuta
STAGE="$(mktemp -d)"
ditto --norsrc --noextattr --noacl "$APP" "$STAGE/$NAME.app"
APP="$STAGE/$NAME.app"
chmod -R u+w "$APP"
find "$APP/Contents/Resources" -type f \( -name '*.so' -o -name '*.dylib' -o -perm -u+x \) -print0 |
    while IFS= read -r -d '' f; do
        if file -b "$f" | grep -q Mach-O; then codesign --force --sign - --timestamp=none "$f"; fi
    done 2>&1 | { grep -v "replacing existing signature" || true; }
codesign --force --sign - --timestamp=none "$APP"
codesign --verify --deep --strict "$APP"

ZIP="dist/${NAME// /}-$VERSION.zip"     # senza spazi: diventa un URL di download
mkdir -p dist
rm -f "$ZIP"
ditto -c -k --keepParent "$APP" "$ZIP"
echo "▸ app $(du -sh "$APP" | cut -f1), zip $(du -h "$ZIP" | cut -f1): $(pwd)/$ZIP"
