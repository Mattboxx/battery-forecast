# Changelog

## Unreleased

- The discharge panel follows new battery readings between Timestral runs. The curve starts at the current
  level and its arrival time is recombined with recent consumption. Saved forecasts still reflect the original run.
- The drain forecast averages the central 10th–90th percentile estimates and skips the separate level-model call
  when measured drain is available. A single learned weight gradually replaces the prior as evidence builds.
- Preferences keep local sensor and app-workload diagnostics and remove the experimental sensor input to
  Timestral. Charging keeps its measured target time and drops the habitual unplug message.

## 0.9.10

- **Charging time from the measured charging.** Replayed on a real laptop's history (151 moments of 18 charges that
  reached 80%), the Timestral time at the charge target was on average 77 minutes off even after correction, and
  missing for more than half of the moments; this PC's earlier charges from the same level were 2.3 minutes off and
  the slope of the current charge about 3. The time at the target now comes from those measurements (earlier charges
  first, then the current slope) and the Timestral curve is retimed to it: 2.5 minutes off on average, 98% within 10
  minutes (2.4 on the 106 charge forecasts the app had saved, against 114 before).
- The slope of the current charge is used after 4 minutes and 2 points instead of 8 minutes, and at any distance
  below an 80% target instead of only within 20 points (same error, measured). Earlier charges no longer wait for it.
- Without a measurement yet (the first minutes of a charge with no comparable earlier charge) the panel says it is
  measuring instead of showing the Timestral time, which in those moments was hours off.
- The level-band correction from past errors no longer applies to charging; Preferences say why.
- Diagnostics: `tilldusk chargetest --db <copy>` measures every saved charge forecast; `replay --charge [--context]`.

## 0.9.9

- **Timestral now forecasts the power, not the percentage.** On battery the level falls exactly as fast as energy
  is used, so the uncertain part is the future drain. The app sends the measured drain (percent per hour, battery
  time only) and Timestral forecasts it; the discharge curve is its integral. Replayed every 10 minutes over a real
  laptop's history (93 moments with an observed arrival at the reserve, 6 discharges), with each forecast learning
  only from earlier ones: the arrival time was on average 13 minutes off (72% within 15 minutes), against 17 minutes
  (63%) for 0.9.8 on the same moments and 27 (38%) for Windows. Timestral alone went from 102 to 13 minutes.
- The level-band correction from past errors is not applied to these curves (it made them worse); they learn
  through the per-PC weight of measured power and Timestral, only from forecasts made the same way. Preferences say
  how many observed arrivals that weight is based on.
- Without an hour of measured drain, and when charging, forecasts work as before. Both options that still act on
  charging were measured before keeping them, by replaying past charging forecasts: learning from past errors
  brought the error at the charge target from 15 to 9 minutes (16 charges, median +8 to +3), and the sensor
  history in Timestral cut charge forecasts more than two hours off from 14 to 5 (65 moments, same median).
- **Learn from past forecast errors** now also governs the discharge weight: off means fixed weights, and Reset
  restarts that learning too. Its help text describes what it does for discharge and for charging.
- Diagnostics: `tilldusk replay --db <copy>` redoes past discharge forecasts with the current engine so that
  `tilldusk backtest` measures the whole chain; `backtest --dump` writes the per-forecast errors as CSV.

## 0.9.8

- **Much closer arrival at the reserve.** Measured on a real laptop's history (49 discharges that actually reached the
  reserve): the Timestral time was on average 86 minutes off (71 too late in the median), Windows' estimate 33, the new
  combined time 21 minutes, with the median error about one minute and 57% of arrivals within 15 minutes.
  - The headline combines the energy left divided by the mean measured power of the current discharge (up to an hour;
    then Windows' own estimate right after unplugging; then this PC's typical power) with the Timestral curve.
  - Each PC learns the weight of the two from the arrivals it has actually observed, separately for short and long
    measured stretches; the curve is retimed to the combined arrival, so chart, headline and alerts agree.
- **Learning from past errors works along the whole discharge.** Each saved forecast is compared with reality in
  5-point spans, credited to 10-point battery-level bands: on many laptops the reported percentage falls much faster
  near empty (2.5 times in the 20-10% band on the test laptop), which a single factor learned on the first 30 minutes
  could not fix. Bounds 0.5-2 per band; bands without enough evidence use broader comparable conditions.
- Learning reads the whole period of saved discharge forecasts instead of the 200 most recent of any kind.
- Battery Forecast's own code is now released under the **GNU GPL version 3 or later** (LICENSE, NOTICE). Till Dusk's
  MIT notice is preserved for the parts derived from it; a GPL section 7 permission covers the bundled third-party
  components. Versions up to 0.9.7 remain available under MIT.
- Diagnostics: `tilldusk backtest --db <copy>` compares Windows, Timestral, the measured power and the combined time
  on an existing history, using only data available before each forecast.

## 0.9.7

- Preferences keep the **Learn from past forecast errors** status current: it refreshes with every new forecast
  while the section is open. Before, a window opened in the first seconds after startup kept showing 0/8 even
  though the forecast already had a ready correction.

## 0.9.6

- **Learn from past forecast errors now actually learns.** It required past forecasts taken with identical
  apps, CPU band, screen and brightness, held stable for most of the following hour; the top apps change
  at almost every forecast, so no comparison was ever accepted (0/8 comparisons, 0/3 sessions). It now looks
  for evidence from the most specific to the broadest conditions (same apps and CPU load, same CPU load and
  screen, similar load, all comparable battery use) and uses the first level with 8 comparisons in 3 sessions.
  The scope used is shown in Preferences. Same bounds as before (±25%), same original curves kept.
- Diagnostic command `tilldusk calibration --db <copy>` reports the comparisons found on an existing history.

## 0.9.5

- Pin the self-contained .NET and Windows Desktop runtimes to the audited 10.0.13 version on both architectures. Newer build-runner SDKs cannot silently change the distributed runtime; license checks continue to reject unreviewed components.

- Keep the AI chart and main duration together, with recent-consumption and Windows estimates inside an optional **Compare other estimates** section. Explain that they are independent calculations and may differ substantially; do not replace one estimate with another to make them match.
- Keep the recent-consumption estimate as the main fallback when AI history is insufficient, without duplicating it as a comparison. Explain Windows-only estimates while the app is still collecting data.
- Identify AI forecasts and their learning state explicitly, including continued-active-use forecasts.
- Derive beyond-horizon wording from the actual remaining chart duration in both charging and discharging, including shortened calibrated curves. Do not promise 24 hours when the chart supports less.
- Preserve the forecasting model, its weights, local calibration, recorded data and estimator calculations. These changes clarify presentation; no accuracy improvement is claimed.
- Synchronize history-store disposal with active SQLite readers and transactions, preventing the connection from closing underneath a background operation during shutdown. A controlled regression reproduces the old race before the fix and verifies that active work completes before closing.

## 0.9.2

- Bounded optional local learning from original saved forecasts and later uninterrupted observations in comparable battery, CPU, display and app-load conditions. Require at least eight disjoint comparisons across three operating sessions, retain both original and issued curves, and offer disable/reset controls. Rate correction is capped at ±25%; it does not retrain Timestral or demonstrate an accuracy gain. A faster correction may shorten the supported forecast horizon.

- Optional local executable workload history with measured per-app CPU and working sets, distinct historical model channels for recurring apps and workload combinations, and matched **whole-PC** power associations. Off by default; no window titles, paths, arguments or invented future app activity. Retention, deletion and explicit CSV export cover the added data.

- Zoom Battery trend around the mouse pointer, pan with the middle button, and reset with visible controls or the keyboard. The percentage scale adapts to the visible range; curve visibility choices and original pinned references remain independent of model calculation.

- Invalidate pre-sleep cached/in-flight forecasts, detect missed wake/hibernation notifications with passive Windows clocks, and keep active-use model time independent of unobserved sleep duration. Preserve raw calendar history; retain genuine awake idle readings.

- Restore optional WMI brightness collection and isolate sensor failures, without adding drivers or dependencies.
- Use time-weighted battery and context history instead of giving diagnostic bursts extra weight. Preserve exact reading times, CPU counter intervals and collection triggers in SQLite schema 7.
- Keep older history; legacy CPU readings without recorded intervals remain available in history/exports but are excluded from model input until new valid intervals are collected.
- Send known sleep/wake, app restart, adapter and battery-change boundaries to the local engine. Block interpolation and continuous-session counts across them; screen off remains distinct from sleep.
- Pass the original target-observation mask to Timestral so held/interpolated target points do not count as measurements. Preserve the current reading as the forecast anchor.
- Verify these rules with functional tests and the real local model. No measured accuracy gain or calibrated uncertainty is claimed. Windows x64 and ARM64 offline installers retain the existing branding, model and component terms.

The existing battery history is retained on upgrade. Schema 7 cannot be opened by 0.9.1: back up local data before downgrading. CPU context needs at least six new nonconstant five-minute bins with recorded intervals (about thirty minutes); older battery/power/memory history remains available.

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
