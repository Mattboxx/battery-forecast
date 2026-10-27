"""Inactivity duration must not become evidence of low active drain. Functional invariants only."""
import json
import numpy as np
import pytest
from battery_forecast import series,protocol


def history(sleep):
    first=[(i*300,90-i*.5,0) for i in range(13)]
    second=[(3600+sleep+i*300,84-i*.5,0) for i in range(13)]
    end=second[-1][0]
    rows=first+second
    s=series.build(rows,end,78,0,discontinuities=[3900,3600+sleep])
    data={'cpu_pct':[(t,float(i%7)*10) for i,(t,_,_) in enumerate(rows)],
          'display_state':[(t,float(i%2)) for i,(t,_,_) in enumerate(rows)]}
    numeric,categorical,_=series.past_covariates(data,s)
    return s,series.active_inputs(s,numeric,categorical),rows


def test_eight_and_twenty_hour_hibernation_have_identical_model_inputs():
    short,packed1,_=history(8*3600)
    long,packed2,_=history(20*3600)
    assert len(long.level)>len(short.level)
    assert short.observed_hours==long.observed_hours==2
    for a,b in zip(packed1,packed2): np.testing.assert_allclose(a,b,equal_nan=True)
    level,ac,mask,num,cat=packed1
    assert len(level)<40 and mask.sum()==26
    assert np.isnan(level[~mask]).all()
    assert num.shape[-1]==cat.shape[-1]==len(level)
    assert np.isnan(ac[~mask]).all()


def test_awake_idle_observations_are_kept_not_confused_with_sleep():
    s=series.build([(i*300,80,0) for i in range(30)],8700,80,0)
    packed=series.active_inputs(s)
    assert len(packed[0])==30 and packed[2].all()


def test_short_unobserved_gap_and_unknown_ac_are_not_fabricated():
    s=series.build([(0,80,None),(600,78,None)],600,78,None)
    level,ac,mask,_,_=series.active_inputs(s)
    assert len(level)==3 and np.isnan(ac).all() and not mask[1]


def test_real_model_does_not_extend_autonomy_just_because_hibernation_was_longer():
    from pathlib import Path
    from battery_forecast.engine import Engine
    model=Path(__file__).resolve().parents[1]/'models/Timestral-28M'
    if not (model/'config.json').exists(): pytest.skip('local model absent')
    engine=Engine(model,threads=2)
    predictions=[]
    for sleep in (8*3600,20*3600):
        s,_,rows=history(sleep)
        req={'type':'forecast','protocol_version':1,'id':1,'now_utc':s.origin,'level':78,'ac':0,
             'samples':rows,'discontinuities_utc':[3900,3600+sleep]}
        response=engine.predict(protocol.parse_request(json.dumps(req).encode()))
        assert response['time_basis']=='active_use'
        assert response['quality']['model_context_steps']<40
        predictions.append([[p['p10'],p['p50'],p['p90']] for p in response['curve']])
    np.testing.assert_allclose(predictions[0],predictions[1],rtol=1e-6,atol=1e-6)
