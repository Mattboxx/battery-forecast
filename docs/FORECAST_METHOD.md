# Battery Forecast: calculation method

Status: experimental, local inference. This describes the unreleased development build.
The current changes correct functional defects; they do not establish predictive superiority.
Scientific backtesting is postponed at the user's request.

## Inputs at the actual reading time

The app reads battery level, adapter state and available energy/power. It takes battery history and
optional system context only up to that reading's timestamp, using the recorded current battery epoch
when a design-capacity change has identified a replacement. Older observations remain in local history
and exports. Detection by design capacity cannot identify every replacement with an identical battery.

Historical battery values are averaged on a five-minute UTC grid. An interval containing both adapter
states, or an unknown state, remains unknown. The last value is the actual current reading.
Missing bins interpolate only when endpoints are at most ten minutes apart and power state is known
and consistent throughout. Longer gaps and unknown power states are preserved. Short unrecorded sleep
periods cannot always be distinguished from dropped samples; this policy is not a sleep detector.
Observed hours and coverage are counted before interpolation. Thirty-minute sessions must span
thirty minutes, rather than counting six grid points as thirty minutes.

Optional context includes battery watts, CPU load/frequency, memory, available temperatures/brightness,
power profile and Windows session display state. Missing sensors are not replaced by estimates.
Categorical profile/display values are never averaged or interpolated. Six observed nonconstant bins
are required before a channel is supplied to the model. Sensor inputs cover history only: future CPU,
temperature, display state and workload are not known. More sensors do not guarantee better forecasts.

## The actual local-model path

The fixed Timestral model receives the battery series, historical context and one explicit future adapter
scenario: remain unplugged or remain plugged in. The app does not retrain the weights. It obtains
P10/P50/P90 level curves and applies the documented physical direction, range and ordering constraints.
Future points start five minutes after the actual request time and end 24 hours after it. Historical bins
retain their UTC grid; the current point and all future labels share the request-time anchor.

When the model instead predicts a habitual recharge or unplug, the existing declared extrapolation
continues from that boundary. Its fallback rate now uses a Theil–Sen-style median pairwise slope from
the latest contiguous phase only. It cannot combine disconnected visits to the charger or bridge a
remaining missing grid value. Pair separations of at least ten minutes reduce sensitivity to percentage rounding.
If the phase is too short or has no usable rate, no rate is invented. The existing extrapolated charging
slowdown and ±25% spread remain heuristics, not newly validated physical or uncertainty models.

## Current-use estimates remain separate

Discharge compares observed available energy above reserve to the median recent battery power when
available. Otherwise it uses a robust percentage slope on the current discharge segment. An immediate
reading replaces a stored duplicate at the same timestamp; known design-capacity changes stop the tail.
Charging uses a robust short local trend only within the existing allowed range below taper, or previous
comparable observed charging intervals. Future observations are excluded and fresh duplicates win.
The app does not blend these methods using arbitrary weights or claim one universally wins.

## Checks and limits

Regression tests exercise mixed AC, endpoint gap length, sparse data, actual session durations,
contiguous phases, noisy endpoints, battery replacement, duplicate readings, request-time bounds,
forecast timestamps and charging inputs. A real Timestral call verifies both numerical context and the
historical categorical display-state channel reach the actual forecaster. Synthetic test histories verify
behavior and integration, not accuracy against future real observations.

P10–P90 is an indicative level band, not a calibrated time-to-threshold confidence interval. Future
workload, charger changes, firmware limits and sensor availability remain uncertain. No empirical error,
superiority, calibrated coverage or energy-saving claim has been measured for these changes.

See [MODEL_CARD.md](MODEL_CARD.md) for thresholds, extrapolation details and model limitations;
[UPSTREAM.md](UPSTREAM.md) and [LICENSE_AUDIT.md](LICENSE_AUDIT.md) for pinned sources and third-party terms.
