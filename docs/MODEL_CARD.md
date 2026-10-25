# Model card — Battery Forecast

Status: **experimental**. No accuracy figures are claimed until `docs/VALIDATION.md` reports measured results.

## What is forecast

- **Target**: battery level (%) on a 5-minute grid, next 24 hours (288 steps).
- **Model**: Timestral-28M (Mistral AI), pinned revision in [UPSTREAM.md](UPSTREAM.md), run locally on CPU.
  Timestral-5M is kept for comparison.
- **Context**: up to 110 days of this PC's own history, recorded by the app (one sample per minute,
  aggregated to 5-minute steps). Gaps longer than 10 minutes stay missing (NaN), never interpolated.
- **Covariate**: power adapter connected (1) / on battery (0) / unknown (NaN). In the future the scenario
  sets it to 0.
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

- Habits need days of data; with less than 2 hours of history or no discharge session the habit forecast
  is marked insufficient and the recent-use estimate is shown instead.
- Data is recorded only while the app runs; sleep and shutdown are gaps, not observations.
- Validation so far: synthetic data only (functional checks). Validation on real sessions is in progress during the preview.

## Licenses

Timestral code: Apache 2.0. Timestral weights: modified MIT license, distributed with its exact text
(it includes a condition on monthly consolidated revenue). See `licenses/`.
