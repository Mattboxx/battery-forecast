"""Punto d'ingresso dell'eseguibile PyInstaller: battery_forecast.exe --model <cartella>."""
import sys

from battery_forecast.worker import main

if __name__ == "__main__":
    sys.exit(main())
