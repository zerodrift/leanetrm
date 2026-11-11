"""Curve-owned captured-hour fixing checks in the runner's disposable fixture.

All prices, identities and collection events are synthetic. UTC operating grids
use the installed New York clock; these checks assert no ISO release timetable.
"""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
import time
import json
from uuid import uuid4

from exact_values import fraction, instant
from power_collection_fixture import Collector
from ppa_demo_terms import profile_terms
from preview_client import initial_frame


def prepare(market,manifest):
    frame = deepcopy(manifest['frames'][0])
    frame['at'] = (datetime.now(timezone.utc) + timedelta(minutes=3)).isoformat()
    manifest['powerSettlementAt'] = frame['at']
    manifest['frames'].append(frame)
    sparse=deepcopy(manifest['frames'][0]);sparse['at']='2020-12-20T16:00:00Z'
    assert not any(f['at']==sparse['at'] for f in manifest['frames'])
    sparse['curveContracts']['test_power_offpeak_product_monthly']=[]
    from interval_curve_fixture import block_contracts, OFF
    excluded={row[2] for row in block_contracts()[OFF]}
    sparse['observationIds']=[oid for oid in sparse['observationIds'] if oid not in excluded]
    manifest['powerProductSparseAt']=sparse['at'];manifest['frames'].append(sparse)

    # This owned synthetic bundle must be effective at the timed valuation too.
    # It changes no market calendar, contract dates or publication convention.
    from preview_fixture import M
    period=f"INSERT INTO market_data.revision_periods VALUES ({M},'2020-01-01','2022-01-01');"
    import re
    pattern=re.compile(r"INSERT INTO market_data.revision_periods VALUES \("+re.escape(M)+r",'2020-01-01','[0-9-]+'\);")
    periods=pattern.findall(market);assert len(periods)==1
    period=periods[0]
    from check_power_product_resolution import prepare_market
    return prepare_market(market.replace(period,f"INSERT INTO market_data.revision_periods VALUES ({M},'2020-01-01','2032-01-01');",1),manifest)



def run_checks(client, query, manifest, frame, original, published, binding):
    client.call('profile', {'profile': 'editor'})
    privileges=query("SELECT bool_or(has_function_privilege(role_name,signature,'EXECUTE')) FROM unnest(ARRAY['openetrm_runtime','openetrm_risk_worker']) role_name CROSS JOIN unnest(ARRAY['power_data.read_prices_pre_portfolio_worker(uuid,timestamptz,timestamptz,timestamptz)','power_data.read_prices_pre_portfolio_worker(uuid,timestamptz,timestamptz,timestamptz,timestamptz)']) signature;")
    assert privileges=='f', privileges
    workspace = client.call('workspace')
    live = next(f for f in workspace['frames'] if instant(f['at']) == instant(manifest['powerSettlementAt']))
    valued_at = instant(live['at'])
    catalog = client.call('price-publications?snapshotId=' + frame['id'])
    source_id = manifest['intervalCurves']['source']
    source = json.loads(query(f"SELECT row_to_json(s)::text FROM power_data.sources s WHERE id='{source_id}';"))
    index = next(i for i in catalog['indices'] if i['key']['code'] == 'test_power_price' and i['quote']['code'] == 'test_power_outright')
    calendar = query(f"SELECT calendar_code FROM ref_data.price_index_definitions WHERE catalog_id='{manifest['referenceCatalogId']}' AND revision_id='{manifest['referenceRevisionId']}' AND code='test_power_price';")
    publication = dict(key=dict(catalogId=catalog['catalogId'], id=str(uuid4()), version=1), references=catalog['references'],
        publisher=dict(key=dict(catalogId=catalog['catalogId'], id=str(uuid4()), version=1), name='Synthetic hourly publisher'),
        name='Synthetic captured hourly LMPs', commodities=[index['commodity']],
        indices=[{k: v for k, v in index.items() if k != 'name'} | dict(symbol='SYNTHETIC_HOURLY', statistic='day_ahead_hourly_total_lmp')],
        calendar=next(c['reference'] for c in catalog['calendars'] if c['reference']['code'] == calendar),
        clock=next(c['reference'] for c in catalog['clocks'] if c['reference']['code'] == source['clock_code']),
        implementation='power_captured_hourly_v1')
    save_publication = dict(requestId=str(uuid4()), snapshotId=frame['id'], expectedVersion=0, definition=publication)
    first_save = client.call('price-publications/save', save_publication)
    assert client.call('price-publications/save', save_publication) == first_save
    terms = dict(settlementPublication=publication['key']['id'], settlementVersion='1', settlementIndex=index['key']['code'],
                 settlementSource=source_id, settlementPolicy='captured_required_for_ended_v1', projectionPolicy='flat_native_mark_replace_actual_v1')
    settled = deepcopy(original)
    next(n for n in settled['nodes'] if n['kind'] == 'power_intervals')['parameters'].update(terms)
    # Read-only preview and saved publication use the same exact binding.
    current = client.call('custom-curves/preview', dict(definition=settled, snapshotId=frame['id']))
    assert current['summary']['anchors'] == 2
    for field, value in [('settlementIndex', 'missing'), ('settlementVersion', '99'), ('projectionPolicy', 'recalibrate')]:
        invalid = deepcopy(settled)
        next(n for n in invalid['nodes'] if n['kind'] == 'power_intervals')['parameters'][field] = value
        client.call('custom-curves/preview', dict(definition=invalid, snapshotId=frame['id']), expected=422)
    draft = client.call('custom-curves/save', dict(definition=settled, snapshotId=frame['id'], requestId=str(uuid4()), layout={},
        id=published['id'], version=2, generation=published['generation'], expectedState='published'))
    assert next(n for n in draft['definition']['nodes'] if n['kind'] == 'power_intervals')['parameters'] == next(n for n in settled['nodes'] if n['kind'] == 'power_intervals')['parameters']
    token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','approver','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
    query(f"""BEGIN;
      UPDATE market_data.custom_curve_nodes SET parameters=jsonb_set(parameters,'{{settlementPolicy}}','\"assume_final\"')
       WHERE(catalog_id,curve_id,version,kind)=('{manifest['catalogId']}','{draft['id']}',3,'power_intervals');
      SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');
      DO $$BEGIN
       PERFORM security.publish_custom_curve('{manifest['catalogId']}','{draft['id']}',3,{draft['generation']});
       RAISE EXCEPTION 'Expected settlement publication refusal' USING ERRCODE='XX000';
      EXCEPTION WHEN SQLSTATE 'P0001' THEN IF SQLERRM<>'power_settlement_binding' THEN RAISE;END IF;END$$;ROLLBACK;""")
    old_known = client.call('interval-series')['knownAt']
    client.call('profile', {'profile': 'approver'})
    client.call('custom-curves/publish', dict(requestId=str(uuid4()), id=draft['id'], version=3, generation=draft['generation']))
    client.call('custom-curves/activation', dict(requestId=str(uuid4()), curveId=draft['id'], expectedRevision=2,
        policy='complete_effective_timeline_v1', reason='Synthetic captured interval pricing',
        segments=[dict(effectiveFrom='2020-01-01', effectiveTo='2032-01-01', version=3, outputNode='result')]))
    collector = Collector(query, manifest, {'pjm': dict(provider='PJM', sourceId=source_id, clock='synthetic_eastern')})
    first = collector.publish('pjm', '2031-11-01', instant('2031-11-01T04:00:00Z'), 24, 60, ['7'] * 24, 1)
    assert instant(first['knownAt']) < valued_at, 'The owned fixture must capture its original before its valuation instant'
    client.call('profile', {'profile': 'trader'})
    def physical(known, one_day=False, snapshot=None):
        return dict(snapshotId=snapshot or live['id'], definitionRevisionId=manifest['revisionId'], referenceCatalogId=manifest['referenceCatalogId'],
            referenceRevisionId=manifest['referenceRevisionId'], scope='draft', selectedIds=[], explicitSubset=False,
            terms=profile_terms(manifest), quantity='1', coverageFrom=binding['deliveryFrom'],
            coverageTo='2031-11-02T04:00:00Z' if one_day else binding['deliveryTo'], knownAt=known,
            pendingActuals='require_actuals_v1', detailMonths=['2031-11-01'])
    old = client.call('ppa/preview', physical(old_known))
    assert old['receipt']['powerLocations'][0]['output']['version'] == 2
    assert fraction(old['valueExcludingOverdue']) == 6080
    known = client.call('interval-series')['knownAt']
    mixed = client.call('ppa/preview', physical(known))
    assert fraction(mixed['valueExcludingOverdue']) == 5288, mixed['valueExcludingOverdue']
    fixed = client.call('ppa/preview', physical(known, True))
    assert fraction(fixed['valueExcludingOverdue']) == -792
    assert fixed['receipt']['powerLocations'][0]['settlement']['sourceId'] == source_id
    print('PASS physical captured pricing: saved output binding, prior activation, 24 actual + 697 forward hours, all-fixed Side PV and exact source evidence', flush=True)
    # Ended hours follow corrections at the selected cutoff, even with an expired
    # construction anchor. They have no live forward-price dependencies.
    past = deepcopy(settled)
    past.update(code='synthetic_ended_hours', name='Synthetic ended captured hours')
    next(n for n in past['nodes'] if n['kind'] == 'power_intervals')['parameters'].update({'from': '2020-12-14', 'to': '2020-12-15'})
    client.call('profile', {'profile': 'editor'})
    missing = client.call('custom-curves/preview', dict(definition=past, snapshotId=frame['id']), expected=422)
    assert missing['code'] == 'interval_actual_missing', missing
    past_first = collector.publish('pjm', '2020-12-14', instant('2020-12-14T05:00:00Z'), 24, 60, ['7'] * 24, 1)
    initial = client.call('custom-curves/preview', dict(definition=past, snapshotId=frame['id']))
    assert initial['summary']['anchors'] == 0 and all(r[2] == '7' and r[-1] == 'Actual' for r in initial['rows'])
    past_second = collector.publish('pjm', '2020-12-14', instant('2020-12-14T05:00:00Z'), 24, 60, ['9'] * 24, 2)
    revised = client.call('custom-curves/preview', dict(definition=past, snapshotId=frame['id']))
    assert revised['summary']['anchors'] == 0 and all(r[2] == '9' for r in revised['rows'])
    assert all(p['actual']['batchId'] == past_second['batchId'] for p in revised['trace']['intervalEvidence'])
    historical=type(client)(client.url)
    historical.bootstrap();historical.call('profile',{'profile':'editor'});historical.call('workspace')
    historical.call('market/context', dict(effectiveOn='2020-12-18', knownAt=past_first['knownAt'], mode='at', selection='latest_correction'))
    retained_frame=initial_frame(historical.call('workspace/navigation'),'2020-12-18')
    retained = historical.call('custom-curves/preview', dict(definition=past, snapshotId=retained_frame['id']))
    assert all(r[2] == '7' for r in retained['rows'])
    print('PASS ended captures: missing-price refusal, expired all-actual anchor, zero forward dependencies, corrected value and retained historical value', flush=True)
    remaining = (valued_at - datetime.now(timezone.utc)).total_seconds()
    if remaining > 0:
        print(f'Waiting {remaining:.0f}s for the owned DA correction boundary', flush=True)
        while datetime.now(timezone.utc) <= valued_at:
            time.sleep(min(1, max(0.01, (valued_at - datetime.now(timezone.utc)).total_seconds())))
    second = collector.publish('pjm', '2031-11-01', instant('2031-11-01T04:00:00Z'), 24, 60, ['9'] * 24, 2)
    assert instant(second['knownAt']) > valued_at
    client.call('profile', {'profile': 'trader'})
    retained_da = client.call('ppa/preview', physical(client.call('interval-series')['knownAt']))
    assert fraction(retained_da['valueExcludingOverdue']) == 5288
    print('PASS DA correction boundary: original known at valuation survives a later visible correction for future delivery', flush=True)
