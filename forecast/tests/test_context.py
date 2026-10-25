import json
import numpy as np
import pytest
from battery_forecast import series, protocol


def context():
    return series.build([(i*300,90-i,0) for i in range(10)],2700,81,0)


def test_sensor_grid_interpolates_short_gaps_but_not_long_gaps():
    s=context()
    data={"cpu_pct":[[i*300,float(i*5)] for i in [0,1,2,5,6,7,8,9]]}
    numeric,categories,names=series.past_covariates(data,s)
    assert numeric.shape==(1,1,10) and categories is None and names==["cpu_pct"]
    assert numeric[0,0,3]==15 and numeric[0,0,4]==20
    data={"cpu_pct":[[i*300,float(i*5)] for i in [0,1,2,7,8,9]]}
    numeric,_,_=series.past_covariates(data,s)
    assert np.isnan(numeric[0,0,3:7]).all()


def test_unavailable_constant_or_future_channels_are_not_invented():
    assert series.past_covariates({"cpu_pct":[[i*300,None] for i in range(10)]},context())[2]==[]
    assert series.past_covariates({"cpu_pct":[[i*300,50] for i in range(10)]},context())[2]==[]
    assert series.past_covariates({"cpu_pct":[[i*300,i] for i in range(10,20)]},context())[2]==[]


def test_categorical_profile_is_historical_and_separate():
    _,categorical,names=series.past_covariates({"power_profile":[[i*300,0 if i<5 else 1] for i in range(10)]},context())
    assert names==["power_profile"] and categorical.shape==(1,1,10)


def test_protocol_rejects_nonfinite_and_unknown_context_and_drops_future():
    req={"type":"forecast","protocol_version":1,"id":1,"now_utc":2700,"level":80,"ac":0,
         "past_covariates":{"cpu_pct":[[300,30],[3000,90]]}}
    parsed=protocol.parse_request(json.dumps(req).encode())
    assert parsed["past_covariates"]["cpu_pct"]==[(300,30)]
    req["past_covariates"]={"invented_sensor":[[300,4]]}
    with pytest.raises(protocol.ProtocolError):protocol.parse_request(json.dumps(req).encode())
    req["past_covariates"]={"cpu_pct":[[300,float('nan')]]}
    with pytest.raises(protocol.ProtocolError):protocol.parse_request(json.dumps(req).encode())
