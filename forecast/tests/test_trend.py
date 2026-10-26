"""Functional regressions with synthetic inputs; no empirical accuracy claims."""
import numpy as np
import pytest

from battery_forecast import series
from battery_forecast.engine import observed_rate, observed_charge_rate, quality_state


@pytest.mark.parametrize("charging", [False, True])
def test_recent_rate_does_not_join_separate_power_phases(charging):
    state = int(charging)
    rows = [(i*300, 60+i*.5, state if i != 10 else 1-state) for i in range(12)]
    s = series.build(rows, 3300, 65.5, state)
    rate = observed_charge_rate if charging else observed_rate
    assert rate(s) is None


@pytest.mark.parametrize("charging", [False, True])
def test_recent_rate_is_robust_to_one_noisy_endpoint(charging):
    sign = 1 if charging else -1
    rows = [(i*300, 60+sign*i*.6, int(charging)) for i in range(12)]
    s = series.build(rows, 3300, rows[-1][1]+4, int(charging))
    rate = observed_charge_rate if charging else observed_rate
    assert rate(s) == pytest.approx(7.2, abs=0.001)


def test_sparse_days_do_not_count_as_hours_of_observed_data():
    rows = [(i*300, 90-i, 0) for i in range(13)]
    s = series.build(rows, 10*86400, 60, 0)
    assert s.observed_days == 10
    assert s.observed_hours < 2
    assert quality_state(s) == "insufficient"


def test_interpolation_uses_endpoint_distance_and_respects_unknown_or_mixed_ac():
    ten = series.build([(0, 80, 0)], 600, 78, 0)
    assert np.isfinite(ten.level).all()
    assert ten.coverage == pytest.approx(2/3, abs=.001)  # filled values are not observations
    fifteen = series.build([(0, 80, 0)], 900, 77, 0)
    assert np.isnan(fifteen.level[1:3]).all()
    unknown = series.build([(0, 80, None)], 600, 78, None)
    assert np.isnan(unknown.level[1])
    changed = series.build([(0, 80, 0), (300, None, 1)], 600, 78, 0)
    assert np.isnan(changed.level[1])
    mixed = series.build([(0, 80, 0), (300, 79, 0), (301, 79, 1)], 600, 78, 0)
    assert np.isnan(mixed.ac[1])


def test_thirty_minute_session_needs_thirty_minutes_not_six_grid_points():
    short = series.build([(i*300, 90-i, 0) for i in range(6)], 1500, 85, 0)
    full = series.build([(i*300, 90-i, 0) for i in range(7)], 1800, 84, 0)
    assert short.discharge_sessions == 0
    assert full.discharge_sessions == 1
