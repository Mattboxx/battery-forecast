import json
import numpy as np
import pytest
from battery_forecast import protocol,series


def context():
    return series.build([(i*300,90-i,0) for i in range(13)],3600,78,0)


def test_individual_app_channels_remain_distinct_and_unknown_absence_is_not_interpolated():
    chrome='app_012345abcdef_cpu';editor='app_abcdef012345_cpu'
    data={chrome:[(i*300,i*3.) for i in range(13) if i!=5],editor:[(i*300,40-i*2.) for i in range(13)]}
    numeric,_,names=series.past_covariates(data,context())
    assert set(names)=={chrome,editor} and numeric.shape==(1,2,13)
    assert np.isnan(numeric[0,names.index(chrome),5])
    assert numeric[0,names.index(editor),5]==30


def test_recurring_profile_codes_keep_48_bit_identity_and_rare_profiles_stay_unknown():
    a,b=2**47+1,2**47+2
    data={'workload_profile':[(i*300,a if i<6 else b if i<12 else 42) for i in range(13)]}
    _,cat,names=series.past_covariates(data,context())
    assert names==['workload_profile'] and cat.dtype==np.float64
    assert cat[0,0,0]==a and cat[0,0,6]==b and np.isnan(cat[0,0,12])


def test_protocol_accepts_only_bounded_opaque_app_identifiers():
    req={'type':'forecast','protocol_version':1,'id':1,'now_utc':3600,'level':78,
         'past_covariates':{'app_012345abcdef_cpu':[[300,20]]}}
    assert 'app_012345abcdef_cpu' in protocol.parse_request(json.dumps(req).encode())['past_covariates']
    req['past_covariates']={'app_chrome_cpu':[[300,20]]}
    with pytest.raises(protocol.ProtocolError): protocol.parse_request(json.dumps(req).encode())
