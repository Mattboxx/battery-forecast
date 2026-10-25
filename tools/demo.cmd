@echo off
rem Avvia Battery Forecast in modalita demo (dati sintetici) dalla build di sviluppo.
rem Se la demo e gia aperta, riporta in primo piano il pannello.
set "EXE=%~dp0..\windows\src\BatteryForecast.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\BatteryForecast.exe"
if not exist "%EXE%" (
  echo Build non trovata: compila prima windows\BatteryForecast.slnx
  pause
  exit /b 1
)
start "" "%EXE%" --demo
