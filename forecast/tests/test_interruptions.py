"""Functional preparation checks. Synthetic inputs; no accuracy measurements."""
import json
import numpy as np
import pytest
from battery_forecast import protocol, series
from battery_forecast.engine import observed_rate, observed_charge_rate


def test_short_gap_does_not_bridge_known_sleep_or_restart():
    rows=[(0,80,0),(600,78,0)]
    plain=series.build(rows,600,78,0)
    broken=series.build(rows,600,78,0,discontinuities=[300])
    assert plain.level[1]==79
    assert np.isnan(broken.level[1]) and np.isnan(broken.ac[1])
    assert broken.observed.tolist()==[True,False,True]


@pytest.mark.parametrize('charging',[False,True])
def test_sessions_and_recent_rate_split_at_boundary_even_without_a_missing_bin(charging):
    ac=int(charging)
    rows=[(i*300,60+(1 if charging else -1)*i,ac) for i in range(12)]
    plain=series.build(rows,3300,rows[-1][1],ac)
    broken=series.build(rows,3300,rows[-1][1],ac,discontinuities=[1500,3000])
    count='charge_sessions' if charging else 'discharge_sessions'
    rate=observed_charge_rate if charging else observed_rate
    assert getattr(plain,count)==1 and rate(plain) is not None
    assert getattr(broken,count)==0 and rate(broken) is None


def test_interpolated_bins_do_not_qualify_as_observed_session():
    rows=[(t,80-t/300,0) for t in (0,600,1200,1800)]
    s=series.build(rows,1800,74,0)
    assert np.isfinite(s.level).all()  # values can be interpolated, provenance is retained
    assert s.observed.sum()==4 and s.discharge_sessions==0
    assert s.observed_hours==0  # isolated observations do not establish continuous observed duration


def test_hold_only_bins_are_not_observations_and_current_reading_remains_observed():
    s=series.build([(i*300,90-i,0,i%2==0) for i in range(7)],1800,84,0)
    assert s.observed.tolist()==[True,False,True,False,True,False,True]
    assert s.discharge_sessions==0


def test_partial_break_bin_is_missing_for_context_but_current_battery_anchor_survives():
    rows=[(i*300,90-i,0) for i in range(10)]
    s=series.build(rows,2850,80,0,discontinuities=[450,2800])
    assert np.isnan(s.level[1]) and not s.observed[1]
    assert s.level[-1]==80 and s.observed[-1]  # fresh current reading after the break
    data={'cpu_pct':[(i*300,float(i)) for i in range(10)]}
    numeric,_,names=series.past_covariates(data,s)
    assert names==['cpu_pct']
    assert np.isnan(numeric[0,0,1]) and np.isnan(numeric[0,0,-1])


def test_numeric_sensor_gap_does_not_bridge_boundary():
    s=series.build([(i*300,90-i,0) for i in range(10)],2700,81,0,discontinuities=[1200])
    data={'cpu_pct':[(i*300,float(i)) for i in range(10) if i!=4]}
    numeric,_,_=series.past_covariates(data,s)
    assert np.isnan(numeric[0,0,4])


def test_protocol_accepts_legacy_triples_and_filters_future_boundaries():
    req={'type':'forecast','protocol_version':1,'id':1,'now_utc':600,'level':70,'ac':0,
         'samples':[[0,80,0]],'discontinuities_utc':[600,300,300,601]}
    out=protocol.parse_request(json.dumps(req).encode())
    assert out['discontinuities']==[300.,600.]
    assert out['samples']==[(0,80,0,True)]
    req['samples']=[[0,80,0,False]]
    assert not protocol.parse_request(json.dumps(req).encode())['samples'][0][3]


@pytest.mark.parametrize('invalid',[[float('nan')],['300'],[True],{},[None]])
def test_protocol_rejects_invalid_boundaries(invalid):
    req={'type':'forecast','protocol_version':1,'id':1,'now_utc':600,'level':70,
         'discontinuities_utc':invalid}
    with pytest.raises(protocol.ProtocolError):
        protocol.parse_request(json.dumps(req).encode())
