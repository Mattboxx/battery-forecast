#!/bin/bash
# Compila l'app (nome in app.conf): app solo barra dei menu, firmata ad-hoc, che chiede le previsioni
# allo script Python del progetto Timestral (la cartella sopra questa).
set -euo pipefail
cd "$(dirname "$0")"
source ./app.conf                     # nome, identificativo e versione: si cambiano solo lì

APP=".build/bundle/$NAME.app"     # il bundle di compilazione sta in .build, l'app vera si installa
CONTENTS="$APP/Contents"
PROJECT="$(cd .. && pwd)"

# --- Ambiente di previsione ---------------------------------------------------
# Python + PyTorch + script vivono in Application Support, non in ~/Documents (vedi Forecast.swift).
# `uv` clona i pacchetti dalla sua cache (APFS): il secondo ambiente occupa poco spazio in più.
RUNTIME="$HOME/Library/Application Support/Autonomia/runtime"
echo "▸ ambiente di previsione in ${RUNTIME}…"
mkdir -p "$RUNTIME"
(cd "$PROJECT" && UV_PROJECT_ENVIRONMENT="$RUNTIME/venv" uv sync --frozen --python 3.12 2>&1 | tail -1)
cp "$PROJECT/scripts/battery_now.py" "$RUNTIME/battery_now.py"

echo "▸ compilazione…"
swift build -c release

echo "▸ assemblo il bundle…"
rm -rf "$APP"
mkdir -p "$CONTENTS/MacOS" "$CONTENTS/Resources"
cp .build/release/Autonomia "$CONTENTS/MacOS/Autonomia"
# Traduzioni (en, it): in Contents/Resources le trova Bundle.main (vedi Localization.swift)
cp -R Resources/*.lproj "$CONTENTS/Resources/"

# Icona: solo se c'è app/AppIcon.icns (nome e icona sono ancora da scegliere)
ICON_KEY=""
if [ -f AppIcon.icns ]; then
    cp AppIcon.icns "$CONTENTS/Resources/AppIcon.icns"
    ICON_KEY="<key>CFBundleIconFile</key><string>AppIcon</string>"
fi

cat > "$CONTENTS/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key><string>$NAME</string>
    <key>CFBundleDisplayName</key><string>$NAME</string>
    <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
    <key>CFBundleExecutable</key><string>Autonomia</string>
    $ICON_KEY
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>1</string>
    <key>CFBundleDevelopmentRegion</key><string>en</string>
    <key>CFBundleLocalizations</key><array><string>en</string><string>it</string></array>
    <key>LSMinimumSystemVersion</key><string>26.0</string>
    <key>LSUIElement</key><true/>
    <key>NSHighResolutionCapable</key><true/>
</dict>
</plist>
PLIST

codesign --force --sign - --timestamp=none "$APP" >/dev/null 2>&1 || true
echo "▸ pronto: $(pwd)/$APP"

# Installazione in ~/Applications: così il sistema la riconosce come app (Spotlight, Comandi
# Rapidi, strumenti di controllo) e il percorso dell'apertura al login resta stabile.
# Con `--no-install` si salta.
if [ "${1:-}" != "--no-install" ]; then
    DEST="$HOME/Applications"
    mkdir -p "$DEST"
    if pgrep -f "$NAME.app/Contents/MacOS/Autonomia" >/dev/null; then
        echo "▸ chiudo l'istanza in esecuzione…"
        pkill -f "$NAME.app/Contents/MacOS/Autonomia" || true
        sleep 1
    fi
    rm -rf "$DEST/$NAME.app"
    cp -R "$APP" "$DEST/$NAME.app"
    /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$DEST/$NAME.app" >/dev/null 2>&1 || true
    echo "▸ installata: $DEST/$NAME.app"
fi
