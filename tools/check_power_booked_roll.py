"""Booked power consumers before qualifying the in-term Friday/Monday bridge."""
from copy import deepcopy
import json
from uuid import uuid4

from check_power_product_resolution import delivery, request
from exact_values import fraction
from preview_fixture import trade_booking


def in_term_booking(client, query, manifest, frame, product):
    client.call('profile', {'profile': 'trader'})
    draft=request(manifest,frame['id'],client.call('interval-series')['knownAt'],'peak',product,
        '2020-12-01T05:00:00Z','2021-01-01T05:00:00Z')
    draft['terms']['delivery'].update({'from':'2020-12-01','to':'2021-01-01'})
    command=dict(operationId=str(uuid4()),snapshotId=frame['id'],tradeId=None,expectedRevision=None,
        bookId='d1000000-0000-0000-0000-000000000001',boundaryAt=None,coverageFrom=draft['coverageFrom'],
        coverageTo=draft['coverageTo'],quantity='1',reason='Synthetic full-month in-term power qualification',
        memo='',booking=trade_booking(),terms=draft['terms'],admission='in_term_fixed_power_contractual_v1')
    for admission in (None,'unknown_admission'):
        refused=client.call('ppa/book',dict(command,operationId=str(uuid4()),admission=admission),expected=422)
        assert refused['code']=='ppa_admission',refused
    for field,value in [('discounting',True)]:
        invalid=deepcopy(command);invalid['operationId']=str(uuid4());invalid['terms'][field]=value
        refused=client.call('ppa/book',invalid,expected=422)
        assert refused['code']=='ppa_admission',refused
    partial=deepcopy(command);partial.update(operationId=str(uuid4()),coverageFrom='2020-12-02T05:00:00Z')
    assert client.call('ppa/book',partial,expected=422)['code']=='ppa_admission'
    legacy=deepcopy(command);legacy.pop('admission');legacy['operationId']=str(uuid4())
    refused=client.call('ppa/book',legacy,expected=422)
    assert refused['code'] in ('ppa_coverage','ppa_booking'),refused
    # The private SQL boundary must refuse malformed/null policy facts too.
    assert query("SELECT NOT has_function_privilege('openetrm_runtime','security.ppa_require_in_term_power(jsonb)','EXECUTE') AND NOT has_function_privilege('openetrm_risk_worker','security.ppa_require_in_term_power(jsonb)','EXECUTE');")=='t'
    payload=dict(command,action='booked',valuationAt=frame['at'])
    checks=[]
    for path,value in [('admission',None),('action','amended'),('terms,discounting',None),('terms,delivery,from',None),('terms,delivery,intervalMinutes',15),('terms,memberships,0,side,quantity,kind',None)]:
        encoded="'"+json.dumps(payload).replace("'","''")+"'::jsonb"
        scalar="'"+json.dumps(value).replace("'","''")+"'::jsonb"
        checks.append("DO $$BEGIN PERFORM security.ppa_require_in_term_power(jsonb_set("+encoded+",'{"+path+"}',"+scalar+"));RAISE EXCEPTION 'Expected explicit admission refusal' USING ERRCODE='XX000';EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'ppa_admission' THEN RAISE;END IF;END$$;")
    query('BEGIN;'+''.join(checks)+'ROLLBACK;')
    booked=client.call('ppa/book',command)
    assert client.call('ppa/book',command)==booked
    stored=client.call('ppa/editor',dict(snapshotId=frame['id'],tradeId=booked['tradeId'],knownAt=client.call('interval-series')['knownAt']))
    assert stored['instruments'][0]['terms']['delivery']==draft['terms']['delivery']
    print('PASS in-term power admission retains the full original December month and idempotent outcome',flush=True)
    hourly=dict(implementation='local_hour_contractual_mw_factors_v1',factors=['2' if 7<=h<15 else '1' for h in range(24)])
    shaped=deepcopy(command);shaped.update(operationId=str(uuid4()),admission='in_term_shaped_power_contractual_v1')
    shaped['terms']['memberships'][0]['side']['quantity']['hours'].update(membership='all',touProduct=None,touVersion=None)
    shaped['terms']['memberships'][0]['side']['quantity']['hourlyProfile']=hourly
    for admission in ('in_term_fixed_power_contractual_v1',):
        refused=client.call('ppa/book',dict(shaped,operationId=str(uuid4()),admission=admission),expected=422)
        assert refused['code']=='ppa_admission',refused
    assert client.call('ppa/book',dict(command,operationId=str(uuid4()),admission='in_term_shaped_power_contractual_v1'),expected=422)['code']=='ppa_admission'
    for profile in (None,dict(hourly,implementation='unknown'),dict(hourly,factors=hourly['factors'][:-1]),dict(hourly,factors=['0']*24),dict(hourly,factors=['-1']+hourly['factors'][1:])):
        invalid=deepcopy(shaped);invalid['operationId']=str(uuid4());invalid['terms']['memberships'][0]['side']['quantity']['hourlyProfile']=profile
        refused=client.call('ppa/book',invalid,expected=422);assert refused['code']=='ppa_profile_shape',refused
    retained=client.call('ppa/book',shaped);assert client.call('ppa/book',shaped)==retained
    stored=client.call('ppa/editor',dict(snapshotId=frame['id'],tradeId=retained['tradeId'],knownAt=client.call('interval-series')['knownAt']))
    assert stored['instruments'][0]['terms']['memberships'][0]['side']['quantity']['hourlyProfile']==hourly
    query("BEGIN;DO $$DECLARE s trading.ppa_sides;BEGIN SELECT * INTO STRICT s FROM trading.ppa_sides WHERE quantity_kind='profile' AND NOT EXISTS(SELECT 1 FROM trading.ppa_side_hourly_profiles p WHERE(p.catalog_id,p.side_id)=(ppa_sides.catalog_id,ppa_sides.id)) LIMIT 1;BEGIN INSERT INTO trading.ppa_side_hourly_profiles VALUES(s.catalog_id,s.id,'local_hour_contractual_mw_factors_v1',array_fill(1::numeric,ARRAY[24]));RAISE EXCEPTION 'Expected fresh-publication refusal' USING ERRCODE='XX000';EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'ppa_profile_publication' THEN RAISE;END IF;END;END$$;ROLLBACK;")
    print('PASS shaped in-term admission: explicit policy, malformed profile refusals, immutable factor readback/retry and old-side retrofit refusal',flush=True)



def booked_forward(client, manifest, frame, product):
    client.call('profile', {'profile': 'trader'})
    known = client.call('interval-series')['knownAt']
    draft = request(manifest, frame['id'], known, 'peak', product,
                    '2031-11-01T04:00:00Z', '2031-12-01T05:00:00Z')
    expected = client.call('ppa/preview', draft)
    assert fraction(delivery(expected)['valueExcludingOverdue']) == 18240
    assert fraction(expected['valueExcludingOverdue']) == 6080
    command = dict(operationId=str(uuid4()), snapshotId=frame['id'], tradeId=None,
        expectedRevision=None, bookId='d1000000-0000-0000-0000-000000000001', boundaryAt=None,
        coverageFrom=draft['coverageFrom'], coverageTo=draft['coverageTo'], quantity='1',
        reason='Synthetic peak power consumer qualification', memo='', booking=trade_booking(), terms=draft['terms'])
    booked = client.call('ppa/book', command)
    assert client.call('ppa/book', command) == booked
    stored = deepcopy(draft)
    stored.update(scope='trades', selectedIds=[booked['tradeId']], explicitSubset=True,
                  terms=None, quantity=None, coverageFrom=None, coverageTo=None,
                  knownAt=client.call('interval-series')['knownAt'])
    result = client.call('ppa/preview', stored)
    assert fraction(delivery(result)['valueExcludingOverdue']) == 18240
    assert fraction(result['valueExcludingOverdue']) == 6080
    assert result['receipt']['powerLocations'][0]['output'] == expected['receipt']['powerLocations'][0]['output']
    assert len(delivery(result)['rows']) == 304
    print('PASS booked graph power: retained peak schedule, 304 hours, gross18240/net6080, exact output and idempotent booking', flush=True)


def shaped_forward(client,manifest,frame,product):
    client.call('profile',{'profile':'trader'})
    draft=request(manifest,frame['id'],client.call('interval-series')['knownAt'],'all',product,'2031-11-01T04:00:00Z','2031-12-01T05:00:00Z')
    profile=dict(implementation='local_hour_contractual_mw_factors_v1',factors=['2' if 7<=h<15 else '1' for h in range(24)])
    draft['terms']['memberships'][0]['side']['quantity']['hourlyProfile']=profile
    expected=client.call('ppa/preview',draft)
    assert fraction(delivery(expected)['valueExcludingOverdue'])==47560
    assert fraction(expected['valueExcludingOverdue'])==9120
    command=dict(operationId=str(uuid4()),snapshotId=frame['id'],tradeId=None,expectedRevision=None,bookId='d1000000-0000-0000-0000-000000000001',boundaryAt=None,coverageFrom=draft['coverageFrom'],coverageTo=draft['coverageTo'],quantity='1',reason='Synthetic firm hourly ATC profile qualification',memo='',booking=trade_booking(),terms=draft['terms'])
    booked=client.call('ppa/book',command);assert client.call('ppa/book',command)==booked
    selected=dict(draft,scope='trades',selectedIds=[booked['tradeId']],explicitSubset=True,terms=None,quantity=None,coverageFrom=None,coverageTo=None,knownAt=client.call('interval-series')['knownAt'])
    retained=client.call('ppa/preview',selected)
    assert fraction(retained['valueExcludingOverdue'])==9120
    assert [fraction(r['quantity']) for r in delivery(retained)['rows']]==[fraction(r['quantity']) for r in delivery(expected)['rows']]
    assert sum(fraction(r['quantity']) for r in delivery(retained)['rows'])==961
    print('PASS booked ATC MW shape: retained November DST961MWh, physical47560, cash38440, net9120 and draft/retained parity',flush=True)


def daily_price_shape(client,query,manifest,frame,lookup_body):
    """A saved mixed-tenor lookup uses the exact existing additive-series reader."""
    from datetime import datetime,timedelta
    from fractions import Fraction
    from zoneinfo import ZoneInfo
    client.call('profile',{'profile':'editor'})
    series=client.call('interval-series/define',dict(requestId=str(uuid4()),code='synthetic_daily_native_offsets',name='Synthetic daily native offsets',kind='additive_price_shape',specification='test_power_outright',clock='synthetic_eastern',intervalMinutes=60))['seriesId']
    start=datetime.fromisoformat('2031-11-20T05:00:00+00:00')
    values=['4' if 7<=(start+timedelta(hours=i)).astimezone(ZoneInfo('America/New_York')).hour<15 else '-4' for i in range(264)]
    revision=dict(requestId=str(uuid4()),seriesId=series,expectedRevision=0,reason='Synthetic daily product price shape',segments=[dict(start='2031-11-20T05:00:00Z',values=values)])
    receipt=client.call('interval-series/revise',revision);assert client.call('interval-series/revise',revision)==receipt
    before=client.call('interval-series')['knownAt']
    body=deepcopy(lookup_body);body['definition'].update(code='synthetic_shaped_product_lookup',name='Synthetic shaped product lookup')
    parameters=next(n for n in body['definition']['nodes'] if n['kind']=='power_intervals')['parameters']
    parameters.update(shapePolicy='daily_native_block_additive_v1',shapeSeries=series)
    preview=client.call('custom-curves/preview',body)
    rows=preview['rows'];assert len(rows)==1008,preview['summary']
    daily=[r for r in rows if r[6]<'2031-12-01']
    assert len(daily)==264
    assert sum(Fraction(r[2]) for r in daily)==15136
    assert next(r[2] for r in daily if r[0].startswith('2031-11-20T12:'))=='69'
    assert next(r[2] for r in daily if r[0].startswith('2031-11-20T20:'))=='61'
    assert {r[2] for r in daily if r[4]=='Off-peak'}=={'42'}
    assert {r[2] for r in rows if r[6]=='2031-12-01'}=={'70','45'}
    saved=client.call('custom-curves/save',dict(body,requestId=str(uuid4()),layout={}))
    assert next(n for n in saved['definition']['nodes'] if n['kind']=='power_intervals')['parameters']['shapeSeries']==series
    # SQL publication must independently reject a retained wrong-kind or hidden source.
    wrong=manifest['intervalCurves']['shapeSeries']['multiplicative']
    for mutation in (f"UPDATE market_data.custom_curve_power_price_shapes SET series_id='{wrong}' WHERE curve_id='{saved['id']}';",
                     f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{manifest['referenceCatalogId']}';"):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','editor','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        query(f"""BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');RESET SESSION AUTHORIZATION;{mutation}
        DO $$BEGIN UPDATE market_data.custom_curve_versions SET state='published' WHERE(catalog_id,curve_id,version)=('{manifest['catalogId']}','{saved['id']}',1);
        RAISE EXCEPTION 'Expected shape publication refusal' USING ERRCODE='XX000';EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM NOT IN('power_price_shape_binding','power_interval_product','power_interval_source','custom_curve_source') THEN RAISE;END IF;END$$;ROLLBACK;""")
    client.call('profile',{'profile':'approver'})
    published=client.call('custom-curves/publish',dict(requestId=str(uuid4()),id=saved['id'],version=1,generation=saved['generation']))
    assert published['state']=='published'
    client.call('profile',{'profile':'editor'})
    # One corrected offset changes calibration, but the old certified cutoff replays.
    before=client.call('interval-series')['knownAt']
    client.call('interval-series/revise',dict(requestId=str(uuid4()),seriesId=series,expectedRevision=1,reason='Synthetic shape correction',segments=[dict(start='2031-11-20T12:00:00Z',values=['20'])]))
    client.call('market/context',dict(effectiveOn='2020-12-18',knownAt=before,mode='at',selection='latest_correction'))
    from preview_client import initial_frame
    historical_frame=initial_frame(client.call('workspace/navigation'),'2020-12-18')
    historical=client.call('custom-curves/preview',dict(body,id=saved['id'],version=1,snapshotId=historical_frame['id']))
    assert historical['rows']==rows
    client.call('market/current',{})
    corrected=client.call('custom-curves/preview',dict(body,id=saved['id'],version=1))
    assert next(r[2] for r in corrected['rows'] if r[0].startswith('2031-11-20T12:'))=='84'
    assert sum(Fraction(r[2]) for r in corrected['rows'] if r[6]=='2031-11-20')==1376
    print('PASS saved daily price shape: exact series FK, 69/61 peak,42 off, monthly70/45 flat, unchanged product means, SQL wrong-kind/hidden-source refusal and correction replay',flush=True)
