"""Synthetic dated-contract booking checks with independent daily cash oracles."""
from copy import deepcopy
from datetime import date, datetime, timedelta, timezone
from fractions import Fraction
import json
from uuid import uuid4

from preview_fixture import CAT, R, REV, identity


def prepare(market, manifest):
    partial = next(f for f in manifest['frames'] if f['at'].startswith('2021-01-15T08:00:00'))
    # At the fixing instant the same official settlement is both today's fixed
    # observation and the live mark for later observations of that contract.
    old_mark = identity('7', 4000000 + 10000 + 101)
    manifest['frames'].append({**partial, 'at': '2021-01-15T16:00:00Z',
        'observationIds': [identity('7', 8700015) if pin == old_mark else pin for pin in partial['observationIds']]})
    definitions = f"""
INSERT INTO market_data.curves SELECT (jsonb_populate_record(NULL::market_data.curves,
    to_jsonb(c)||'{{"code":"future_carry","name":"Synthetic final carry"}}'::jsonb)).*
    FROM market_data.curves c WHERE(c.catalog_id,c.revision_id,c.code)=('{CAT}','{REV}','future');
INSERT INTO market_data.curve_members SELECT catalog_id,revision_id,'future_carry',contract_id,ordinal
    FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{CAT}','{REV}','future');
-- Older-schema replay checks use this shared fixture without the new consumer.
DO $fixture$ BEGIN
 IF to_regclass('trading.swap_contract_observations') IS NOT NULL THEN
  INSERT INTO market_data.curve_expiry_rules VALUES('{CAT}','{REV}','future_carry','freeze_final_settlement_v1');
 END IF;
END $fixture$;
"""
    market = market.replace('UPDATE market_data.revisions SET', definitions + '\nUPDATE market_data.revisions SET', 1)
    manifest['schedules'].append({**next(s for s in manifest['schedules'] if s['curve'] == 'future'), 'curve': 'future_carry'})
    for frame in manifest['frames']:
        frame['curveContracts']['future_carry'] = list(frame['curveContracts']['future'])
    # Each contractual fixing has a distinct price. A re-roll, latest-price lookup
    # or delivery-month substitution therefore cannot reproduce the daily sum.
    rows = []
    for day in range(1, 32):
        month = 1 if day < 28 else 2
        series = identity('9', month * 100 + 1)
        observation = identity('7', 8700000 + day)
        at = f'2021-01-{day:02d}T16:00:00Z'
        rows.append(
            f"INSERT INTO market_data.observations VALUES ('{CAT}','{observation}',"
            f"'{REV}','{series}',{R},'fixture','contract_daily_{day}','1',"
            f"'daily_settlement','final','{at}','{datetime.now(timezone.utc).isoformat()}',"
            f"{10 + day},NULL);")
    rows.append(f"INSERT INTO market_data.observations VALUES ('{CAT}','{identity('7', 8700100)}',"
                f"'{REV}','{identity('9', 101)}',{R},'fixture','contract_final','1',"
                f"'final_contract_settlement','final','2021-01-28T15:30:00Z',"
                f"'{datetime.now(timezone.utc).isoformat()}',50,NULL);")
    from check_contract_observation_sql import prepare as prepare_sql_parity
    market = prepare_sql_parity(market, manifest)
    return market.rsplit('COMMIT;', 1)[0] + '\n' + '\n'.join(rows) + '\nCOMMIT;\n'


def run(c, context, template, query, run_worker=None):
    body = deepcopy(template)
    body['operationId'] = str(uuid4())
    for name in ('first', 'second'):
        side = body['scheduled'][name]
        side['periods'] = side['periods'][:1]
        side['periods'][0]['amount'] = '31'
    body['scheduled']['first']['periods'][0]['fixed']['price'] = '0'
    floating = body['scheduled']['second']['periods'][0]['floating']
    observations = []
    for ordinal in range(31):
        start = date(2021, 1, 1) + timedelta(days=ordinal)
        observations.append(dict(ordinal=ordinal, **{'from': start.isoformat()},
                                 to=(start + timedelta(days=1)).isoformat(),
                                 fixingAt=start.isoformat() + 'T16:00:00Z'))
    floating.update(curve='future', index=None, settlement=None,
                    resetFrequency='daily', averagingMethod='arithmetic',
                    contractObservations=dict(
                        implementation='contract_observation_source_v1',
                        historyPolicy='visible_corrections_at_cutoff_v1',
                        role='daily_settlement', expiryPolicy='require_live',
                        selection=dict(mode='prompt_offset', contract=None, promptOffset=0,
                                       roll='contract_expiry_roll_v1', boundaries=None),
                        observations=observations))
    from check_contract_observation_authoring import run as check_authoring
    check_authoring(c, context, body)
    from check_contract_observation_sql import run as check_sql_parity
    if query is not None:
        check_sql_parity(c, context, body, query)
    preview = c.call('swaps/preview', body)['valuation']
    saved = c.call('swaps/book', body)
    assert c.call('swaps/book', body) == saved
    if query is not None:
        from check_contract_observation_integrity import run as check_integrity
        check_integrity(query)
    original = c.call('swaps/editor', dict(context=context, tradeId=saved['tradeId'], revision=None))
    assert original['valuation']['presentValue'] == preview['presentValue']
    if query is not None:
        instrument = original['state']['instrumentId']
        retained = json.loads(query(f"""SELECT jsonb_agg(jsonb_build_object(
            'from',r.observation_from,'delivery',k.delivery_from,'sideDelivery',r.delivery_from,'contract',b.contract_id)
            ORDER BY r.observation_from)
            FROM trading.instrument_side_schedules m
            JOIN trading.side_schedule_rows r ON (r.catalog_id,r.schedule_id)=(m.catalog_id,m.schedule_id)
            JOIN trading.side_schedule_bindings b ON (b.catalog_id,b.schedule_id,b.row_ordinal)=(r.catalog_id,r.schedule_id,r.ordinal)
            JOIN market_data.contracts k ON (k.catalog_id,k.revision_id,k.id)=(b.source_catalog_id,b.definition_revision_id,b.contract_id)
            WHERE m.instrument_id='{instrument}' AND r.operation='contract_observation_price' AND b.port='market';"""))
        assert len(retained) == 31, retained
        for day, row in enumerate(retained, 1):
            month = 1 if day < 28 else 2
            assert row['contract'] == identity('5', month * 5 + 1), row
            assert row['from'] == f'2021-01-{day:02d}', row
            assert row['delivery'] == f'2021-{month + 1:02d}-01', row
            assert row['sideDelivery'] == '2021-01-01', row
    workspace = c.call('workspace')
    partial_frame = next(f for f in workspace['frames'] if f['at'].startswith('2021-01-15T08:00:00'))
    partial = deepcopy(body)
    partial.update(operationId=str(uuid4()), snapshotId=partial_frame['id'])
    partial_value = c.call('swaps/preview', partial)['valuation']
    assert Fraction(partial_value['presentValue']) == sum(range(11, 25)) + 13 * Fraction('3.05') + 4 * Fraction('3.10')
    partial_saved = c.call('swaps/book', partial)
    partial_read = c.call('swaps/editor', dict(context={**context, 'snapshotId': partial_frame['id']},
                                              tradeId=partial_saved['tradeId'], revision=None))
    assert partial_read['valuation']['presentValue'] == partial_value['presentValue']
    assert partial_read['state']['instrumentId'] == original['state']['instrumentId'], 'Market sample evidence must not change reusable economics'
    close = next(f for f in workspace['frames'] if f['at'].startswith('2021-02-01T08:00:00'))
    later_context = {**context, 'snapshotId': close['id']}
    later = c.call('swaps/editor', dict(context=later_context, tradeId=saved['tradeId'], revision=None))
    assert Fraction(later['valuation']['presentValue']) == sum(range(11, 42)), later['valuation']
    # The same booked instrument must replay at its original context after prompt
    # has rolled, rather than mutate its selected contracts on the later read.
    replay = c.call('swaps/editor', dict(context=context, tradeId=saved['tradeId'], revision=None))
    assert replay['valuation']['presentValue'] == preview['presentValue']
    missing = deepcopy(body)
    missing['snapshotId'] = close['id']
    missing['scheduled']['second']['periods'][0]['floating']['contractObservations']['observations'][0]['fixingAt'] = '2021-01-01T17:00:00Z'
    c.call('swaps/preview', missing, expected=422)
    exact = deepcopy(body)
    exact['operationId'] = str(uuid4())
    exact_source = exact['scheduled']['second']['periods'][0]['floating']
    exact_source['contractObservations']['selection'] = dict(
        mode='exact', contract=dict(revision=dict(catalogId=CAT, revisionId=REV), id=identity('5', 6)),
        promptOffset=None, roll=None, boundaries=None)
    c.call('swaps/preview', exact, expected=422)
    exact_source['contractObservations']['expiryPolicy'] = 'carry_final_settlement'
    c.call('swaps/preview', exact, expected=422)  # The source curve must also permit final carry.
    exact_source['curve'] = 'future_carry'
    exact_saved = c.call('swaps/book', exact)
    carried = c.call('swaps/editor', dict(context=later_context, tradeId=exact_saved['tradeId'], revision=None))
    assert Fraction(carried['valuation']['presentValue']) == sum(range(11, 38)) + 4 * 50, carried['valuation']
    if query is not None:
        correction = str(uuid4())
        previous = identity('7', 8700001)
        query(f"""INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,
            reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,
            role,publication_state,observed_at,received_at,value,corrects_id)
            SELECT catalog_id,'{correction}',definition_revision_id,series_id,reference_catalog_id,
            reference_revision_id,source_code,source_event_id,'{correction}',role,publication_state,
            observed_at,clock_timestamp(),value+10,id FROM market_data.observations
            WHERE catalog_id='{CAT}' AND id='{previous}';""")
        from check_contract_observation_sql import reject_stale_actual_pin
        reject_stale_actual_pin(body, query)
        corrected = c.call('swaps/editor', dict(context=later_context, tradeId=saved['tradeId'], revision=None))
        assert Fraction(corrected['valuation']['presentValue']) == 816
        retained_context = {**later_context, 'knowledgeMode': 'retained', 'contextId': later['context']['contextId']}
        old_knowledge = c.call('swaps/editor', dict(context=retained_context, tradeId=saved['tradeId'], revision=None))
        assert Fraction(old_knowledge['valuation']['presentValue']) == 806
    from check_contract_observation_settlement import run as check_settlement
    settlement_fixture = check_settlement(c, context, body, query)
    from check_contract_observation_risk import run as check_risk
    check_risk(c, context, saved, partial_saved, exact_saved, query, run_worker, settlement=settlement_fixture)
    print('PASS dated-contract booking: explicit prompt roll, historical daily samples, exact retry and retained replay')
