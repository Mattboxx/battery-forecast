# Changelog

## 0.9.0-alpha.2

- Manual target times and four extra targets, quiet hours and custom alerts.
- Global shortcut that toggles the panel (open/close), recorded by pressing the keys in Preferences, with Win key
  support and a clear message when Windows or another app already uses the combination.
- Plugged in with the charge held by a charge limit (or a full battery): the panel shows the Timestral forecast
  "if you unplug now" with its chart, and explains that the charge is paused by a limit. Computed when the panel
  opens or the adapter is connected, never by the background timer.
- Engine errors on a request now reach the app at once instead of waiting for the timeout; a real-engine test
  checks requests exactly as the app sends them.
- "Open on hover" for the tray icon (on by default), and adjustable panel scale and width.
- Compact panel: icon-only toolbar with tooltips; sensor details moved out of the panel.
- Preferences reorganized into sections (Forecast, Panel and shortcut, Notifications, Appearance, Data and privacy,
  General) with explanatory icons; touchpad scrolling no longer races through the page.
- Charging forecast with Timestral ("stay plugged in" scenario) learned from past charges, with habitual-unplug
  handling and the recent charging rate as comparison/fallback; chart probing by mouse/keyboard, summary copy and
  local diagnostics.
- Optional CPU, battery power, memory, brightness, power profile and available temperature history; historical Timestral covariates with no invented future inputs.
- Original copyright restored and complete offline legal inventory, with fail-closed packaging checks and installer verification in CI.
- No accuracy improvement claimed; unsupported temperatures remain unavailable.


## 0.9.0 preview — first Windows release of Battery Forecast

First public preview of the Windows fork of [Till Dusk](https://github.com/mattBonfanti/till-dusk).

- Tray app with a panel: time to the reserve with a cautious/optimistic range, level at your evening time,
  24-hour chart, estimate at the current rate and Windows' own estimate.
- Local [Timestral](https://github.com/mistralai/timestral) forecast on CPU, offline; habitual-recharge handling
  so a usual plug-in time is not mistaken for zero consumption.
- The model runs only when needed (at most every 30 minutes in the background on battery, never with battery
  saver on) and closes after each forecast.
- Alerts: about 60/30/15 minutes before the reserve, low battery, charged, unusual power use.
- History and battery health window, CSV export, data deletion.
- Four themes (Dawn, Mist, Dusk, Midnight) or Windows' own, Italian and English.
- Updates from the app: GitHub releases, installer verified with SHA-256.
- Per-user installer for x64 and ARM64, no administrator rights.

Known limits: accuracy on real laptops is still being measured; installers are not digitally signed yet.
