"""Motore di previsione di Battery Forecast.

Il nucleo (series, physics, protocol) non importa PyTorch: si può provare senza modello.
engine e worker caricano Timestral solo quando servono.
"""

PROTOCOL_VERSION = 1
STEP_SECONDS = 300            # griglia a 5 minuti, come l'originale macOS
HORIZON_STEPS = 24 * 12       # 24 ore: oltre non è verificato
DEFAULT_RESERVE = 5.0         # "riserva" = 5%, come l'originale
