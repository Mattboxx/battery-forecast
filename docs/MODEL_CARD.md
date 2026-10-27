# Model card — Battery Forecast

Status: **experimental**. No accuracy figures are claimed until `docs/VALIDATION.md` reports measured results.

## What is forecast

- **Target**: battery level (%) on a 5-minute grid, up to the next 24 hours (288 native steps).
- **Model**: Timestral-28M (Mistral AI), pinned revision in [UPSTREAM.md](UPSTREAM.md), run locally on CPU.
  Timestral-5M is kept for comparison.
- **Context**: up to 110 days of this PC's own history, recorded by the app (one sample per minute,
  aggregated to 5-minute steps). Gaps longer than 10 minutes stay missing (NaN), never interpolated.
- **Covariate**: power adapter connected (1) / on battery (0) / unknown (NaN). In the future the discharge scenario
  sets it to 0; the charging scenario sets it to 1.
- **Scenario**: "usual use, without plugging in the adapter from now on".
- **Output**: 10th, 50th and 90th percentile of the level at each step; the first time each one reaches the
  reserve threshold (default 5%).

## Post-processing (applied to the future only)

1. **Physical constraints**: levels clipped to 0–100, never above the current level, never rising, and
   percentiles kept ordered.
2. **Habitual recharge** (heuristic, added in this port): Timestral forecasts the level *following the
   user's habits, recharges included*. In the no-charge scenario, constraint 1 would turn a habitual
   recharge into a flat line, i.e. invent zero consumption. When the raw median rises by at least 2 points
   for at least 10 minutes, that step is reported as `habitual_recharge_utc`; from there the curve
   continues at the average rate the model forecast until then (or, if that is too short or too low, the
   rate observed in the last hour), with ±25% for the cautious/optimistic band. The UI draws that part
   dashed and says so. If no reliable rate exists, the curve is not extended and the UI says the habits
   do not tell how much would be used on battery.

   Found on 2026-10-25 with synthetic histories: without this step the habit forecast stayed above the
   reserve for 24 hours while the battery was draining at 11%/h. It is a design choice to be measured
   against real sessions, not a validated improvement.

## What the numbers mean

- The **band** is the range between the 10th and 90th percentile of the *level* at each time. The
  times at which those curves reach the reserve form an indicative cautious/optimistic range. They are
  **not** a calibrated 80% interval for the time-to-reserve.
- Estimates are about reaching the **reserve threshold**, not about when Windows or the firmware shuts
  the PC down.
- Estimated clock times are rounded to 5 minutes.

## Other estimates shown next to it

- **Recent use** (`RecentRate`, C#): energy above the reserve divided by the median discharge power of
  the last 10 minutes, or a robust (Theil–Sen) slope of the percentage over the last continuous
  discharge (≥ 15 minutes and ≥ 2 points). Used on first run, when history is insufficient and when the
  engine is unavailable, and always shown as a comparison.
- **Windows**: the operating system's own remaining-time estimate, when reported.

## Known limitations

- Habits need days of data; with less than 2 hours of observed grid data or no discharge session the habit forecast
  is marked insufficient and the recent-use estimate is shown instead.
- Data is recorded only while the app runs; sleep and shutdown are gaps, not observations.
- Validation so far: synthetic data only (functional checks). Validation on real sessions is in progress during the preview.

## Licenses

Timestral code: Apache 2.0. Timestral weights: modified MIT license, distributed with its exact text
(it includes a condition on monthly consolidated revenue). See `licenses/`.

## Charging forecast (Windows port)

While charging and below the user's target (50–100%, default 80%; it does not control the firmware),
Timestral forecasts the level with the **"stay plugged in" scenario**: the same history, grid and
historical context as the discharge forecast, with the adapter covariate set to connected for the future.

- **Constraints**: 0–100, never below the current level, never decreasing, percentiles ordered.
- **Target time**: first time each curve reaches the target; the earliest arrival comes from the 90th
  percentile, the latest from the 10th. The same caveat applies: not a calibrated interval.
- **Habitual unplug** (heuristic, mirror of the habitual recharge): when the raw median drops by at least
  2 points for 10 minutes, the model is forecasting that the user usually unplugs there. From that step the
  curve approaches 100% exponentially, starting at the average charging rate forecast until then (or the
  rate observed in the last hour), ±25%, so it slows down near full instead of assuming a constant rate.
  The UI draws that part dashed and says so.
- **Quality**: insufficient with less than 2 hours of observed grid data or no observed charging session (at least
  30 minutes plugged in with 2 points gained); then the observed-data estimator below is shown instead.
- First check with synthetic histories (2026-10-26): 50%→80% forecast in about 58 minutes against an ideal
  45; the cautious end of the range was very wide. Functional check only, not accuracy.

### Recent charging rate (comparison and fallback)

A separate **observed-data estimator** is always shown next to the Timestral charging forecast and is used
alone when the history is insufficient.
At least six continuous observations over eight minutes with two percentage points of increase
are required. A robust recent slope may project at most 20 further points, only up to 80%.
For higher or more distant targets, at least two observed charging sessions must cover both the
current level and target with matching design capacity and similar full capacity. Their median
observed interval is used; this captures an observed final slowdown rather than assuming a
constant charging rate through 100%. Gaps over three minutes, AC changes, decreases and large
sensor jumps split charging intervals. Missing data produces a collecting-data message.

These intervals do not establish that the adapter, thermal state or workload match. No calibrated
uncertainty or accuracy claim is made. Real-session validation for charging remains pending.

## Historical system context (experimental)

Windows optionally records aggregate CPU utilization and reported frequency, memory load, battery
charge/discharge watts, display brightness, power profile and available temperatures. Battery temperature
uses the read-only battery-class driver query. CPU temperature is read only from an already installed
OpenHardwareMonitor/LibreHardwareMonitor WMI provider. ACPI thermal zones are a distinct signal and
are never labelled CPU or battery temperature. Missing, unsupported or access-denied data stays null.
No kernel driver, elevated helper, window titles, process paths/arguments or online telemetry are introduced.
Executable names are recorded only when the separate app-workload option is explicitly enabled.

Optional probes are isolated: an unavailable WMI provider or a denied thermal query does not block
brightness or the battery-class temperature probe. WMI uses omitted COM parameters and a rewindable
result set compatible with .NET enumeration. The diagnostic CLI reports per-probe failures separately
from readings. A local hardware check confirmed brightness collection; temperature availability still
depends on installed providers, firmware and permissions. No accuracy gain is inferred from this check.

Sensor context is stored separately in SQLite schema 7, under the same retention/deletion settings.
Five-minute time-weighted means align to the battery grid; mixed power-profile/display bins are missing. Six observed bins
and nonconstant values are required per channel. Numeric gaps up to ten minutes can interpolate when
AC state is unchanged. Longer gaps stay missing. Profiles are categorical, with no interpolation.
The fixed Timestral API receives numeric `past_covariates` and categorical
`categorical_past_covariates`; their lengths cover **history only**. CPU load, power, brightness and
temperatures are not assumed known in the future. Only the chosen adapter scenario remains a known
future covariate. `context_features` in each response/cache identifies channels actually passed to the
model. Collection and model use can be switched off separately in Preferences.

New rows preserve CPU interval start and exact reading time, collection trigger and optional-probe
acquisition time. Only completed CPU intervals of at most 150 seconds are integrated; intervals crossing
known interruptions are rejected. Legacy CPU rows without intervals are retained for export/history but
excluded from model inputs. Instantaneous readings have bounded support to the next reading, origin,
interruption or 150 seconds. These explicit resampling rules prevent bursts from being weighted by row
count; they do not establish a predictive accuracy gain. See [calculation method](FORECAST_METHOD.md).

A real-model functional test checks that sensor arrays reach the forecaster with historical dimensions.
This establishes integration, **not an accuracy improvement**. Rolling-origin comparisons with and
without context, grouped by laptop/workload and using only observations available at each origin,
are still required. More channels may help, do nothing or worsen forecasts; no claim is made before
those measurements exist. Sensor collection overhead and additional inference cost also need field
measurements. GetSystemTimes reports the active processor group on systems with more than 64 CPUs.

### Optional workload history per executable

App workload recording is off by default and remains local. On enabled context ticks it samples accessible
process CPU counters and working sets, groups them by executable name and retains up to twelve groups.
PID/start-time checks prevent reused PIDs from inheriting old CPU usage; those identifiers are not stored.
CPU is normalized to the machine's logical processor count, uses recorded 1–150 second intervals and
resets across continuity breaks/opt-out. First, missing, denied or restarted measurements stay unknown.
Sampling is bounded to 512 processes; protected and short-lived processes may be absent. Working sets
can count shared memory more than once. The app and packaged inference worker are excluded from app
profiles, while total PC battery power/CPU still include their real resource use.

The model can receive CPU and memory histories for up to six currently observed apps as separate numeric
channels with opaque stable identifiers, plus aggregate visible-process load and a recurring top-app-set
categorical profile. It receives no executable-name text, paths, titles or arguments. Individual app
absence is missing and is not interpolated into a fictitious load. Channels still need six observed bins
and variation; each workload-profile code needs six bins. Profile identifiers retain 48-bit precision.
Future app activity, CPU, GPU and memory are not assumed known. Functional integration with the real
model is verified; improved accuracy or per-process energy attribution has not been established.

Data and privacy lists current app loads and, after enough comparable observations, median **whole-PC**
discharge watts for similar app CPU bands, other-CPU load, app mix and available display/brightness state.
It requires six distinct five-minute bins and excludes interruption bins. This is an observational
association, never a claim that a process consumed those watts. These summaries are rebuilt from local
history; process data follows context retention/wipe and appears only in an explicit context CSV export.
The diagnostic ZIP continues to omit history and process names. Turning the option off also excludes
stored app channels from subsequent model requests. No sensitive workload detail is added to the popup.


### Inspecting collection without changing the compact popup

Preferences → Data and privacy shows current sensor availability, per-channel saved observations,
last saved time and the channels listed by the applicable forecast response. An explicit refresh obtains
new readings; unavailable values remain unavailable. Collection is enabled by default, separately from
model use, with a default 60-second context interval configurable from 30 to 120 seconds. Battery history
keeps its existing one-minute cadence. Optional firmware/WMI temperature and brightness probes can be
disabled while aggregate CPU, memory and battery power remain collected.

The three history charts show observed five-minute means for battery watts (discharge positive, charge
negative), CPU load and memory use. They are not forecasts; missing values and gaps over ten minutes are
not joined. The chart window is selectable from 1 to 168 hours. Hover shows a compact observed-value caption; a click pins an outlined diamond and writes its values inline below the chart while the hover probe remains independent. Demo values are explicitly synthetic and
stored separately. No hardware values are added to the forecast popup.

A local read-only integration check against collected history confirmed that the real model consumed
available variable power/CPU/memory channels and returned its 288-point curve. This verifies the data path,
not a causal relationship or an accuracy gain. Unsupported temperature/brightness channels were absent,
not replaced by zero. Device availability and optional-probe latency remain hardware-dependent.

### Session display state

Windows `GUID_SESSION_DISPLAY_STATUS` notifications supply 0=off, 1=on, 2=dimmed; unknown until
reported. Registration is passive, without changing Windows power settings or polling. Context recording
opt-out also disables persistence of display transitions. Migration leaves existing rows NULL. The model
receives historical categorical observations only: mixed or unknown five-minute bins stay missing, and
no future display states are invented. Display state is session-wide, not individual monitor telemetry.

See [Microsoft's power-setting definitions](https://learn.microsoft.com/en-us/windows/win32/power/power-setting-guids)
and [registration API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerpowersettingnotification).
The physical screen-off/on cycle has not yet been manually verified on this machine.

### Operational trend corrections (0.9.1)

See [the calculation method](FORECAST_METHOD.md) for the current pipeline. Forecast points now begin
exactly five minutes after the request timestamp, through 24 hours; historical bins remain on their UTC
grid. Battery history and sensor input are bounded at that same timestamp and the forecast uses the
current recorded battery epoch. Historical views and exports retain older batteries' observations.
Mixed/unknown AC bins stay unknown. Interpolation requires consistent known AC across the gap and
at most ten minutes **between endpoints**. Coverage and observed hours exclude interpolated points;
a qualifying 30-minute session must actually span 30 minutes. Fallback extrapolation uses a robust
slope from the latest contiguous phase, without joining separate AC visits. Immediate readings win
over stored duplicates in recent-rate estimates.

Version 0.9.2 additionally sends known suspend/resume, app exit/start, adapter and battery
replacement boundaries to the engine. They block target/context interpolation, session continuity and
the engine's recent-rate fallback. Means straddling a boundary are excluded; the current battery reading
remains the anchor. Screen off is not suspension. The original target-observation mask is passed to
Timestral: held/interpolated target bins have zero observation weight and cannot qualify observed
sessions. Functional tests cover the protocol and a real model call; predictive superiority remains
unmeasured. See [calculation method](FORECAST_METHOD.md) for time weighting and legacy data handling.

The active-use model input packs unobserved inactive stretches into missing separators, aligning all
channels and masks. Forecast hours mean continued active use from now; calendar history and issued
timestamps remain unchanged. Otherwise identical observed blocks with eight or twenty hours of
hibernation produce identical model arrays and level curves in a functional regression. Actual awake
idle observations are kept. No sleep consumption is invented. Native elapsed/unbiased clocks detect
missed sleep/hibernate events and clock changes; old cached or in-flight forecasts are invalidated on
continuity breaks. Physical sleep/hibernate tests on individual laptops remain necessary.

These are functional correctness changes checked with regression tests and the real inference engine.
Scientific comparisons are postponed at the user's request; superiority and accuracy are unmeasured.

### Original forecasts beside observed readings

The first settings page displays saved responses without recalculating them from subsequent history.
New accepted responses are archived locally and atomically with their run metadata, including issue
and context timestamps, original quantile points, scenario and model revision. The archive follows
history retention and deletion. Existing summary-only runs do not gain invented historical curves. The genuine last cached response from an older version is preserved once with its original timestamp.

A point difference is shown only against an observation after issue time and inside the saved horizon.
Power-scenario changes and interruptions are flagged. The chart covers the previous/next 12 hours;
its selector lists up to 100 recent responses from the last 24 hours. This is an exploratory individual
comparison, not scientific validation, a calibrated error score or evidence of predictive superiority.

Battery trend supports pointer-anchored wheel zoom down to fifteen minutes, bounded middle-button
panning and +/-/reset controls (keyboard +, −, 0). The visible percentage scale adapts when zoomed.
Refreshing the same saved forecast preserves the viewport; selecting a different one resets it.
Navigation changes no stored observations, issued curves or model calculations.

### Local error correction (0.9.2)

An optional bounded correction compares original usable forecasts with later continuous Windows
observations in comparable battery, CPU, display/brightness and, when opted in, app-load conditions.
It requires eight non-overlapping comparisons across three operating sessions. Sleep, gaps, changed
power scenarios, different model revisions, saturated endpoints and unavailable future observations
cannot become error evidence. The median observed/predicted movement ratio adjusts the curve's time
axis by at most ±25%. It does not retrain Timestral, attribute watts to a process, or establish accuracy.
See [the calculation method](FORECAST_METHOD.md#optional-local-correction-from-past-errors-092)
for exact selection rules and limits.

Original and issued curves are retained separately when a correction is applied; future learning uses
the unadjusted curve rather than learning its own correction. The comparison page shows the issued
curve, unchanged after saving. The future curve never extends beyond data supported by the native
forecast; a faster correction can shorten its horizon below 24 hours. P10–P90 remains indicative,
not a calibrated interval. Data and privacy shows whether evidence is sufficient and offers disable
and reset controls. Reset keeps past forecasts but excludes old evidence; deleting history removes it.
Workloads that change often may never collect enough matching evidence. Future app activity remains
unknown. All new model and correction behavior is experimental pending real-session measurements.
