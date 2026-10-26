# Changelog

## 0.9.1

- Independently show or hide observed and predicted curves in Battery trend; remember the choices without running the model.

- Claim manual refresh clicks before scheduling so slow executors cannot replay clicks after the first calculation. Keep the refresh explanation inline without a button tooltip.

- Open settings on Battery trend: original issued forecasts beside observed readings, clear legends, individual differences and condition-change warnings. Save full responses locally with retention/deletion, and preserve the genuine last cached response when upgrading; never invent missing historical forecasts.
- Render the unchanged brand artwork as a vector in window icons and headers for high-DPI clarity.
- Remove the copy-battery-summary action and identify the popup's automatic gold marker as the time goal.

- Correct the forecast time origin, bound every model input to the same reading, and isolate replaced-battery epochs.
- Keep mixed/unknown AC bins missing; interpolate only within ten-minute endpoint gaps with a consistent known power state.
- Count observed coverage before interpolation and require actual session durations; use robust contiguous recent slopes for declared extrapolation.
- Prefer immediate readings over duplicates and exclude future charging samples. These are functional fixes; predictive superiority remains unmeasured.

- Suppress the tray tooltip while the forecast panel is visible; restore its latest text on close.
- Record Windows session display on/off/dimmed events and expose an observed-state chart in Data and privacy.
  Preserve old history as unknown; pass display state as historical categorical context, never future data.
- Keep graph hover captions above the plotting area and pinned values below it.

- Optional evening/time goals, preserving saved times and the main forecast when disabled.
- Manual local-model recalculation from the far-right button in the popup, with fresh readings and no duplicate requests while busy.
- Separate pinned outlined diamond and moving chart probe; outside clicks clear the reference. Compact hover captions; clicked references appear inline below the chart. No focus border is drawn.
- Data/privacy sensor checks, saved observation counts and actual model-channel reporting; observed watts/CPU/memory history charts with a configurable hour window.
- Context collection interval (30–120 seconds) and independent optional temperature/brightness probes. Battery history remains at one minute.
- Real sensor-to-model integration verified locally; no measured accuracy improvement claimed.

## 0.9.0

- Preserve complete offline Intel OpenMP and ArmPL agreements/notices, verified against the exact bundled DLLs.
- Include version-specific Microsoft Visual C++ runtime original terms, with pinned provenance and file hashes.
- Show the applicable component terms in the Italian/English installer; updates open a visible wizard; the legacy alpha.2 updater shows a modal component-terms dialog before installation. Unattended installs require explicit component-term acceptance.
- Map every frozen engine DLL/PYD to legal documents; reject unknown native components, changed vendor binaries and missing installer terms.
- Omit OS-supplied UCRT/API-set copies on supported Windows versions; retain the bundled Python, .NET and model.
- Identify Mattboxx as the Windows publisher while retaining Matteo Bonfanti’s original copyright and Mistral AI attribution.
- Present a static interface screenshot first, with the hover animation in its own feature section on the website and both READMEs.
- No prediction/model changes or measured accuracy claim in this update.

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
