# Battery Forecast: calculation method

Status: experimental, local inference. This describes version 0.9.2.
The current changes correct functional defects; they do not establish predictive superiority.
Scientific backtesting is postponed at the user's request.

## Inputs at the actual reading time

The app reads battery level, adapter state and available energy/power. It takes battery history and
optional system context only up to that reading's timestamp, using the recorded current battery epoch
when a design-capacity change has identified a replacement. Older observations remain in local history
and exports. Detection by design capacity cannot identify every replacement with an identical battery.

Historical battery values use time-weighted means on a five-minute UTC grid. An interval containing both adapter
states, or an unknown state, remains unknown. The last value is the actual current reading.
Missing bins interpolate only when endpoints are at most ten minutes apart and power state is known
and consistent throughout. Longer gaps and unknown power states are preserved. Short unrecorded sleep
periods cannot always be distinguished from dropped samples; this policy is not a sleep detector.
Observed hours and coverage are counted before interpolation. Thirty-minute sessions must span
thirty minutes, rather than counting six grid points as thirty minutes.

Recorded suspend/resume, app exit/start, adapter transitions and detected battery replacement are
explicit boundaries sent to the engine. Interpolation, session counts and recent-rate fallback cannot
join opposite sides. Bins straddling a boundary are discarded because their means cannot separate
the phases; the fresh current battery reading remains the anchor. Historical context in a straddling
bin stays missing, even if the current battery anchor is available. Display-off/dim notifications are
not suspension boundaries: collection can continue while the screen is off.

The original-observation mask survives resampling and interpolation. A hold-only/interpolated target
bin does not increase observed hours or qualify a continuous session; Timestral receives the mask
through its native `observation_mask` argument and gives such target positions zero observation weight.
Qualifying sessions require contiguous original observed bins over at least thirty minutes. Short numeric
sensor interpolation remains a declared estimate and is also blocked by boundaries. No future sensor
observations are introduced.

New context rows preserve the CPU counter interval, exact reading timestamp, collection trigger and
optional-probe acquisition time (SQLite schema 6). CPU averages are weighted by their measured durations,
split at bin boundaries, using only completed measurements available at the request origin. Intervals
longer than 150 seconds or spanning a recorded interruption are excluded. Legacy CPU rows without
intervals remain in storage/exports and can appear in history, but are excluded from model input.
No historical counter intervals are invented during migration.

Instantaneous battery/context readings use a bounded hold until the next reading, request origin,
recorded interruption or 150 seconds, whichever comes first. Means divide by supported time rather
than row count, so diagnostic bursts do not gain extra votes. This is an explicit resampling assumption,
not a claim that an instantaneous reading was continuously measured. A final point with zero duration
can anchor its bin. Historic CPU charts may use legacy point support; they are not duration measurements.

The JSON Lines v1 request accepts additive optional fields: a fourth boolean `observed` element on each
`samples` row (legacy triples default to true), and `discontinuities_utc`, a sorted set of UTC timestamps
bounded at the request origin. The engine rejects nonfinite/invalid boundaries. The app and engine must
be packaged from matching sources to enforce these rules; older engines cannot implement new rules
merely by accepting unknown JSON fields. SQLite schema 6 adds metadata without rewriting old samples.

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

## Visible comparison with the original response

Accepted responses now save their full curve and metadata locally, atomically with the existing run
record. The new first settings page overlays gold observed readings and the selected purple original
prediction, with the P10–P90 indicative band. Original curves remain unchanged when new calculations
arrive. Points before issue time are not used for displayed differences. AC-scenario changes and data
interruptions are identified; observations are not joined across known interruptions or gaps over
three minutes. Isolated observations remain visible as dots. The page updates while visible when a
new battery reading or model result arrives, without launching extra inference; a pinned reference
survives updates of the same selected curve.

Schema 5 adds the response archive without generating historical forecasts. Retention and wipe include
it. Old summary rows are preserved, with no fabricated curves. The genuine last cached response from an older version is imported once, retaining its original model-generation timestamp. The UI lists up to 100 forecasts issued
in the last 24 hours and plots a 24-hour window from 12 hours ago to 12 hours ahead. This feature is an
individual visual comparison requested by the user, separate from postponed scientific backtesting.

Curve visibility is controlled by two independent saved checkboxes. Hiding a curve also hides its markers and band; saved curves and model inputs remain unchanged. Manual refresh requests are claimed before scheduling, so delayed worker execution cannot replay clicks received while busy. A deterministic delayed-executor regression checks this behavior.
