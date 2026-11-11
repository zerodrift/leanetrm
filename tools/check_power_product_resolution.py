"""Product masks through physical Side price resolution; owned synthetic data."""
from copy import deepcopy
from datetime import timedelta
from fractions import Fraction
from zoneinfo import ZoneInfo

from exact_values import fraction, instant
from ppa_demo_terms import profile_terms

ZONE = ZoneInfo('America/New_York')


def request(manifest, snapshot, known, membership, product, start, end):
    terms = profile_terms(manifest)
    hours = terms['memberships'][0]['side']['quantity']['hours']
    hours.update(membership=membership, touProduct=product['id'] if membership != 'all' else None,
                 touVersion=product['version'] if membership != 'all' else None)
    return dict(snapshotId=snapshot, definitionRevisionId=manifest['revisionId'],
        referenceCatalogId=manifest['referenceCatalogId'], referenceRevisionId=manifest['referenceRevisionId'],
        scope='draft', selectedIds=[], explicitSubset=False, terms=terms, quantity='1', coverageFrom=start,
        coverageTo=end, knownAt=known, pendingActuals='require_actuals_v1', detailMonths=['2031-11-01'])


def delivery(result):
    return next(s for s in result['bindings'][0]['sides'] if s['role'] == 'energy_delivery')['measures']


def check_rows(result, expected_starts, price, state):
    rows = delivery(result)['rows']
    assert [instant(r['interval']['start']) for r in rows] == expected_starts
    assert all(fraction(r['quantity']) == 1 for r in rows)
    assert all(fraction(r['amount']) == Fraction(price(at)) for r,at in zip(rows,expected_starts))
    assert sum(fraction(r['amount']) for r in rows) == sum(Fraction(price(start)) for start in expected_starts)
    assert all(p['resolution']['state'] == state for r in rows for p in r['priceComponents'])
    return rows


def forward_products(client, query, manifest, frame, product):
    assert query("SELECT NOT has_function_privilege('openetrm_runtime','security.power_interval_products(uuid,uuid,timestamptz)','EXECUTE') AND NOT has_function_privilege('openetrm_risk_worker','security.power_interval_products(uuid,uuid,timestamptz)','EXECUTE');")=='t'
    client.call('profile', {'profile': 'trader'})
    known = client.call('interval-series')['knownAt']
    start, end = instant('2031-11-01T04:00:00Z'), instant('2031-12-01T05:00:00Z')
    cells = [start + timedelta(hours=n) for n in range(721)]
    def peak(at):
        local = at.astimezone(ZONE)
        return local.weekday() < 5 and local.date().isoformat() != '2031-11-27' and 7 <= local.hour < 23
    prices = lambda at: 60 if peak(at) else 40
    values = {}
    # Keep the session in current mode; only the operation's explicit cutoff is
    # earlier. A later graph publication cannot prove this product at that cutoff.
    earlier=request(manifest,frame['id'],manifest['powerGraphProductBeforeKnownAt'],'peak',product,start.isoformat(),end.isoformat())
    assert client.call('ppa/preview',earlier,expected=422)['code']=='ppa_tou_knowledge'
    draft=deepcopy(product);draft['version']=manifest['powerGraphProductDraftVersion']
    assert client.call('ppa/preview',request(manifest,frame['id'],known,'peak',draft,start.isoformat(),end.isoformat()),expected=422)['code']=='ppa_tou_knowledge'
    for membership, count in [('all', 721), ('peak', 304), ('off', 417)]:
        selected = [at for at in cells if membership == 'all' or peak(at) == (membership == 'peak')]
        assert len(selected) == count
        result = client.call('ppa/preview', request(manifest, frame['id'], known, membership, product, start.isoformat(), end.isoformat()))
        check_rows(result, selected, prices, 'Projected')
        values[membership] = fraction(delivery(result)['valueExcludingOverdue'])
    assert values['all'] == values['peak'] + values['off'] == 34920
    # A peak-only trade does not consume the off curve. The fixture deliberately
    # omits its marks and its frame view, while retaining its exact definition.
    sparse = next(f for f in client.call('workspace')['frames'] if instant(f['at']) == instant(manifest['powerProductSparseAt']))
    selected = [at for at in cells if peak(at)]
    result = client.call('ppa/preview', request(manifest, sparse['id'], known, 'peak', product, start.isoformat(), end.isoformat()))
    check_rows(result, selected, prices, 'Projected')
    # A Sunday-only peak trade has no selected hours and needs no price input.
    result = client.call('ppa/preview', request(manifest, sparse['id'], known, 'peak', product,
        '2031-11-02T04:00:00Z', '2031-11-03T05:00:00Z'))
    assert delivery(result)['rows'] == [] and fraction(result['valueExcludingOverdue']) == 0
    assert len(result['receipt']['powerLocations'])==1
    print('PASS power product forwards: graph-only exact TOU, earlier explicit cutoff/draft refusal, all721/peak304/off417, no unused off marks, zero-hour Sunday peak', flush=True)


LOCATION = 'test_product_hub'
INDEX = 'test_product_lmp'
PEAK = 'test_product_peak'
OFF = 'test_product_off'


def prepare_market(market, manifest):
    from preview_fixture import M, R, CAT, identity
    from interval_curve_fixture import HOLIDAYS
    source_id = identity('d', 796900)
    refs = f"""INSERT INTO ref_data.locations SELECT(jsonb_populate_record(NULL::ref_data.locations,to_jsonb(r)||jsonb_build_object('code','{LOCATION}','name','Synthetic product test hub'))).* FROM ref_data.locations r WHERE(catalog_id,revision_id,code)=({R},'test_hub_a');
INSERT INTO ref_data.commodity_locations SELECT(jsonb_populate_record(NULL::ref_data.commodity_locations,to_jsonb(r)||jsonb_build_object('location_code','{LOCATION}'))).* FROM ref_data.commodity_locations r WHERE(catalog_id,revision_id,commodity_code,location_code)=({R},'test_power','test_hub_a');
INSERT INTO ref_data.price_index_definitions SELECT(jsonb_populate_record(NULL::ref_data.price_index_definitions,to_jsonb(r)||jsonb_build_object('code','{INDEX}','location_code','{LOCATION}','calendar_code','{HOLIDAYS}'))).* FROM ref_data.price_index_definitions r WHERE(catalog_id,revision_id,code)=({R},'test_power_price');
INSERT INTO ref_data.settlement_targets VALUES({R},'test_product_target','index','{INDEX}',NULL);
"""
    for day in ('2020-11-26', '2020-12-25', '2021-01-01'):
        refs += f"INSERT INTO ref_data.calendar_days VALUES({R},'{HOLIDAYS}','{day}',false,'NERC holiday') ON CONFLICT DO NOTHING;\n"
    definitions = []
    for branch, code in enumerate((PEAK, OFF)):
        definitions.append(f"INSERT INTO market_data.curves VALUES({M},'{code}',{R},'test_power','{LOCATION}','test_power_outright','test_hourly_eastern','test_product_target','direct','fixture','trader_mark','Synthetic product resolution {branch}');")
        for month in range(6):
            year, number = (2020, 11 + month) if month < 2 else (2021, month - 1)
            from datetime import date, datetime, timezone
            first = date(year, number, 1)
            end = date(year + number // 12, number % 12 + 1, 1)
            contract, series = identity('d', 796000 + branch * 100 + month * 2), identity('d', 796001 + branch * 100 + month * 2)
            expiry = datetime(year, number, 1, tzinfo=timezone.utc) - timedelta(days=2)
            definitions += [f"INSERT INTO market_data.contracts VALUES({M},'{contract}',{R},'forward','{first}','{end}','{expiry.isoformat()}',NULL);",
                f"INSERT INTO market_data.series VALUES({M},'{series}','{code}_{month}',{R},'{contract}','test_power_outright',NULL);",
                f"INSERT INTO market_data.curve_members VALUES({M},'{code}','{contract}',{month});"]
    definitions.append(f"INSERT INTO power_data.sources(id,catalog_id,name,provider,dataset,market,point,minutes,reference_catalog_id,reference_revision_id,location_code,clock_code,currency_code,quantity_code) SELECT '{source_id}',catalog_id,'Synthetic RT product capture','PJM','rt_hrl_lmps','RT','synthetic_product_hours',60,reference_catalog_id,reference_revision_id,'{LOCATION}',clock_code,currency_code,quantity_code FROM power_data.sources WHERE id='{manifest['intervalCurves']['source']}';")
    assert market.count('UPDATE ref_data.revisions SET') == market.count('UPDATE market_data.revisions SET') == 1
    market = market.replace('UPDATE ref_data.revisions SET', refs + '\nUPDATE ref_data.revisions SET', 1)
    market = market.replace('UPDATE market_data.revisions SET', '\n'.join(definitions) + '\nUPDATE market_data.revisions SET', 1)
    manifest['powerProductSource'] = source_id
    ended = deepcopy(manifest['frames'][0]); ended['at'] = '2021-04-02T16:00:00Z'
    assert not any(f['at'] == ended['at'] for f in manifest['frames'])
    manifest['frames'].append(ended); manifest['powerProductEndedAt'] = ended['at']
    return market


def actual_products(client, query, manifest, frame, product):
    """Use ordinary physical lookup and captured-price rules, with no forward marks."""
    import json
    from uuid import uuid4
    from datetime import date, datetime, timezone
    from power_collection_fixture import Collector
    client.call('profile', {'profile': 'editor'})
    catalog = client.call('price-publications?snapshotId=' + frame['id'])
    member = next(i for i in catalog['indices'] if i['key']['code'] == INDEX and i['quote']['code'] == 'test_power_outright')
    ref = lambda code: dict(revision=catalog['references'], code=code)
    publication = dict(key=dict(catalogId=catalog['catalogId'], id=str(uuid4()), version=1), references=catalog['references'],
        publisher=dict(key=dict(catalogId=catalog['catalogId'], id=str(uuid4()), version=1), name='Synthetic product publisher'),
        name='Synthetic RT product prices', commodities=[member['commodity']],
        indices=[{k: v for k, v in member.items() if k != 'name'} | dict(symbol='SYNTHETIC_RT', statistic='real_time_hourly_total_lmp')],
        calendar=ref('synthetic_eastern_holidays'), clock=ref('synthetic_eastern'), implementation='power_captured_hourly_v1')
    client.call('price-publications/save', dict(requestId=str(uuid4()), snapshotId=frame['id'], expectedVersion=0, definition=publication))
    node = lambda id, kind, parameters=None, inputs=None: dict(id=id, kind=kind, parameters=parameters or {}, inputs=inputs or {})
    definition = dict(code='synthetic_rt_products', name='Synthetic RT product resolution', output='result', nodes=[
        node('peak', 'source', dict(curve=PEAK)), node('off', 'source', dict(curve=OFF)),
        node('hours', 'power_intervals', dict(implementation='flat_forward_only_v1', profileMapping='exact_native_grid_terms_v1',
            product=product['id'], productVersion=str(product['version']), profile='test_hourly_eastern', market='RT',
            settlementPublication=publication['key']['id'], settlementVersion='1', settlementIndex=INDEX,
            settlementSource=manifest['powerProductSource'], settlementPolicy='captured_required_for_ended_v1',
            projectionPolicy='flat_native_mark_replace_actual_v1', **{'from':'2020-11-01','to':'2021-04-01'}), dict(peak='peak', offPeak='off')),
        node('result', 'output', inputs=dict(values='hours'))])
    saved = client.call('custom-curves/save', dict(requestId=str(uuid4()), snapshotId=frame['id'], definition=definition, layout={}))
    client.call('profile', {'profile': 'approver'})
    client.call('custom-curves/publish', dict(requestId=str(uuid4()), id=saved['id'], version=1, generation=saved['generation']))
    client.call('power-location-bindings', dict(requestId=str(uuid4()), definitionRevisionId=manifest['revisionId'], location=LOCATION, market='RT',
        deliveryFrom='2020-11-01T04:00:00Z', deliveryTo='2021-04-01T04:00:00Z', curveId=saved['id'], policy='follow_location_activation_v1',
        reason='Synthetic product resolution checks', segments=[dict(effectiveFrom='2020-01-01', effectiveTo='2032-01-01', version=1, outputNode='result')]))
    collector = Collector(query, manifest, {'rt': dict(provider='PJM', sourceId=manifest['powerProductSource'], clock='synthetic_eastern')})
    client.call('profile', {'profile': 'trader'})
    def cells(day):
        first = datetime.combine(day, datetime.min.time(), ZONE).astimezone(timezone.utc)
        last = datetime.combine(day + timedelta(days=1), datetime.min.time(), ZONE).astimezone(timezone.utc)
        return [first + timedelta(hours=n) for n in range(int((last-first).total_seconds()/3600))]
    def preview(day, membership, known=None, selected=None, expected=200):
        grid = cells(day); snapshot = selected or frame
        body = request(manifest, snapshot['id'], known or client.call('interval-series')['knownAt'], membership, product, grid[0].isoformat(), (grid[-1]+timedelta(hours=1)).isoformat())
        body['terms']['delivery'].update({'from':day.replace(day=1).isoformat(),'to':(day.replace(day=28)+timedelta(days=4)).replace(day=1).isoformat()})
        body['terms']['memberships'][0]['side']['location'] = LOCATION
        body['detailMonths'] = [day.replace(day=1).isoformat()]
        return client.call('ppa/preview', body, expected=expected)
    def partial(day, chosen):
        # Trusted sparse capture fixture. Certification uses real commit time;
        # no publication/finality assertion or collector completeness is inferred.
        token = str(uuid4()); c = manifest['catalogId']; grid = cells(day)
        rows = ','.join(f"('{token}','{manifest['powerProductSource']}','{at.isoformat()}','{(at+timedelta(hours=1)).isoformat()}',{at.astimezone(ZONE).hour+1},1,'{c}')" for at in grid if chosen(at))
        query(f"""BEGIN;
          INSERT INTO power_data.attempts(id,job_id,generation,worker_pid,security_generation,from_date,through_date,started_at,state,finished_at,error_code,catalog_id)
          SELECT '{token}',id,generation,pg_backend_pid(),(SELECT generation FROM security.epoch),'{day}','{day}',clock_timestamp(),'succeeded',clock_timestamp(),NULL,catalog_id FROM power_data.jobs WHERE id='{collector.jobs['rt']}';
          INSERT INTO power_data.batches(id,catalog_id,publication_xid) VALUES('{token}','{c}',pg_current_xact_id());
          INSERT INTO power_data.prices(batch_id,source_id,starts_at,ends_at,price,provider_revision,catalog_id) VALUES {rows};COMMIT;""")
        query(f"SELECT power_data.certify('{c}');")
    is_peak = lambda at: 7 <= at.astimezone(ZONE).hour < 23
    weekday, next_day = date(2020,12,14), date(2020,12,15)
    partial(weekday, is_peak)
    answer = preview(weekday,'peak')
    check_rows(answer,[at for at in cells(weekday) if is_peak(at)],lambda at:at.astimezone(ZONE).hour+1,'Actual')
    assert fraction(answer['valueExcludingOverdue']) == -392
    assert preview(weekday,'off',expected=422)['code'] == 'interval_actual_missing'
    partial(next_day,lambda at:not is_peak(at))
    answer = preview(next_day,'off')
    check_rows(answer,[at for at in cells(next_day) if not is_peak(at)],lambda at:at.astimezone(ZONE).hour+1,'Actual')
    assert fraction(answer['valueExcludingOverdue']) == -268
    assert preview(next_day,'peak',expected=422)['code'] == 'interval_actual_missing'
    assert fraction(preview(date(2020,12,19),'peak')['valueExcludingOverdue']) == 0
    print('PASS RT product demand: only selected peak/off actuals required; missing selected actual refuses; empty weekend peak uses no marks',flush=True)
    for n in range(31):
        day = date(2020,12,1)+timedelta(days=n); grid=cells(day)
        collector.publish('rt',day.isoformat(),grid[0],len(grid),60,[str(at.astimezone(ZONE).hour+1) for at in grid],1)
    ended=next(f for f in client.call('workspace')['frames'] if instant(f['at'])==instant(manifest['powerProductEndedAt']))
    holiday = date(2020,12,25)
    for membership, expected_sum, count in [('all',300,24),('peak',0,0),('off',300,24)]:
        result=preview(holiday,membership,selected=ended)
        assert len(delivery(result)['rows'])==count
        assert sum(fraction(r['amount']) for r in delivery(result)['rows'])==expected_sum
    # Whole-month totals come from an independent local-clock hour-sum oracle.
    grid=cells(date(2020,12,1)); body=request(manifest,ended['id'],client.call('interval-series')['knownAt'],'all',product,grid[0].isoformat(),'2021-01-01T05:00:00Z')
    body['terms']['delivery'].update({'from':'2020-12-01','to':'2021-01-01'});body['detailMonths']=['2020-12-01'];body['terms']['memberships'][0]['side']['location']=LOCATION
    for membership,count,total in [('all',744,9300),('peak',352,5456),('off',392,3844)]:
        h=body['terms']['memberships'][0]['side']['quantity']['hours'];h.update(membership=membership,touProduct=product['id'] if membership!='all' else None,touVersion=product['version'] if membership!='all' else None)
        result=client.call('ppa/preview',deepcopy(body));rows=delivery(result)['rows']
        assert len(rows)==count and sum(fraction(r['amount']) for r in rows)==total
        assert all(fraction(r['amount'])==instant(r['interval']['start']).astimezone(ZONE).hour+1 for r in rows)
    ended=next(f for f in client.call('workspace')['frames'] if instant(f['at'])==instant(manifest['powerProductEndedAt']))
    for day,count,total,selected in [(date(2020,11,1),25,325,frame),(date(2021,3,14),23,276,ended)]:
        grid=cells(day);collector.publish('rt',day.isoformat(),grid[0],len(grid),60,[str(n+1) for n in range(len(grid))],1)
        for membership in ('all','peak','off'):
            result=preview(day,membership,selected=selected);rows=delivery(result)['rows']
            assert len(rows)==(0 if membership=='peak' else count)
            assert sum(fraction(r['amount']) for r in rows)==(0 if membership=='peak' else total)
            if membership!='peak':
                assert [instant(r['interval']['start']) for r in rows]==grid
                assert [fraction(r['amount']) for r in rows]==list(range(1,count+1))
        if count==25:
            rows=delivery(preview(day,'off',selected=selected))['rows']
            repeated=[r for r in rows if instant(r['interval']['start']).astimezone(ZONE).hour==1]
            assert len(repeated)==2 and [fraction(r['amount']) for r in repeated]==[2,3]
    print('PASS actual product matrix: December744/352/392h and9300/5456/3844USD, holiday off-only, DST23/25 distinct UTC hours',flush=True)
    # A correction confined to peak hours changes neither the off-peak product
    # nor a read at the certified cutoff preceding that correction.
    day=date(2020,12,16);grid=cells(day);cutoff=client.call('interval-series')['knownAt']
    collector.publish('rt',day.isoformat(),grid[0],24,60,
        [str(at.astimezone(ZONE).hour+1+(100 if is_peak(at) else 0)) for at in grid],2)
    for membership,old,change in [('all',300,1600),('peak',248,1600),('off',52,0)]:
        selected=[at for at in grid if membership=='all' or is_peak(at)==(membership=='peak')]
        historical=preview(day,membership,known=cutoff)
        check_rows(historical,selected,lambda at:at.astimezone(ZONE).hour+1,'Actual')
        current=preview(day,membership)
        check_rows(current,selected,lambda at:at.astimezone(ZONE).hour+1+(100 if is_peak(at) else 0),'Actual')
        assert sum(fraction(r['amount']) for r in delivery(historical)['rows'])==old
        assert sum(fraction(r['amount']) for r in delivery(current)['rows'])==old+change
    print('PASS product corrections: peak/all change1600USD, off unchanged, earlier certified cutoff retains original hourly prices',flush=True)
