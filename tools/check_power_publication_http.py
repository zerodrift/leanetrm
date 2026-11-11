"""Inspect existing captured hourly rows through exact publication metadata.

This is a compatibility/knowledge test, not a sourced market seed. The real
2021 Eastern DST grid is shared with the existing collected-price fixture.
"""
from copy import deepcopy
from uuid import uuid4

from exact_values import instant


def prepare_market(market, manifest):
    """Deliberate incompatible quote for a refusal check, not a market convention."""
    rc, rr = manifest['referenceCatalogId'], manifest['referenceRevisionId']
    sql = f"""INSERT INTO ref_data.quote_specifications
      SELECT catalog_id,revision_id,'test_power_scaled_quote',commodity_code,kind,quantity_code,currency_code,
      2,trading_tick,clearing_increment,minimum,maximum FROM ref_data.quote_specifications
      WHERE catalog_id='{rc}' AND revision_id='{rr}' AND code='test_power_outright';
      INSERT INTO ref_data.price_index_definitions
      SELECT catalog_id,revision_id,'test_power_scaled_index',commodity_code,location_code,currency_code,
      quote_unit_code,2,calendar_code FROM ref_data.price_index_definitions
      WHERE catalog_id='{rc}' AND revision_id='{rr}' AND code='test_power_price';\n"""
    # Historical PJM membership calendar: NERC's six published 2020 dates.
    # These are delivery holidays, not an exchange trading/expiry calendar.
    sql += f"""INSERT INTO ref_data.calendars VALUES('{rc}','{rr}','pjm_2020_nerc','synthetic_eastern',96,'2020-01-01','2021-01-01');
      INSERT INTO ref_data.price_index_definitions
      SELECT catalog_id,revision_id,'pjm_2020_captured_index',commodity_code,location_code,currency_code,
      quote_unit_code,quote_multiplier,'pjm_2020_nerc' FROM ref_data.price_index_definitions
      WHERE catalog_id='{rc}' AND revision_id='{rr}' AND code='test_power_price';\n"""
    for day in ['2020-01-01', '2020-05-25', '2020-07-04', '2020-09-07', '2020-11-26', '2020-12-25']:
        sql += f"INSERT INTO ref_data.calendar_days VALUES('{rc}','{rr}','pjm_2020_nerc','{day}',false,'NERC 2020 additional off-peak day');\n"
    marker = 'UPDATE ref_data.revisions SET'
    assert market.count(marker) == 1
    return market.replace(marker, sql + marker, 1)


def run_checks(client, query, manifest, snapshot, first, second, first_prices, second_prices):
    client.call('profile', {'profile': 'editor'})
    client.call('price-publications/preview', dict(snapshotId=snapshot, definition=None, occurrence=None), expected=422)
    catalog = client.call('price-publications?snapshotId=' + snapshot)
    source_id = manifest['powerActual']['pjm']['sourceId']
    source = next(s for s in client.call('power-imports')['sources'] if s['id'] == source_id)
    index = next(i for i in catalog['indices'] if i['key']['code'] == 'test_power_price' and i['location']['code'] == source['location_code']
                 and i['unit']['quantity']['code'] == source['quantity_code']
                 and i['unit']['currency']['code'] == source['currency_code']
                 and i['unit']['multiplier'] == '1')
    calendar = query("SELECT calendar_code FROM ref_data.price_index_definitions WHERE "
                     f"catalog_id='{catalog['references']['catalogId']}' AND revision_id='{catalog['references']['revisionId']}' "
                     f"AND code='{index['key']['code']}';")
    definition = dict(key=dict(catalogId=catalog['catalogId'], id=str(uuid4()), version=1), references=catalog['references'],
        publisher=dict(key=dict(catalogId=catalog['catalogId'], id=str(uuid4()), version=1), name='Synthetic capture inspector'),
        name='Captured hourly compatibility check', commodities=[index['commodity']],
        indices=[{k: v for k, v in index.items() if k != 'name'} | dict(symbol='CAPTURED_PRICE', statistic='hourly_total_lmp')],
        calendar=next(c['reference'] for c in catalog['calendars'] if c['reference']['code'] == calendar),
        clock=next(c['reference'] for c in catalog['clocks'] if c['reference']['code'] == source['clock_code']),
        implementation='power_placeholder_v1')
    client.call('price-publications/save', dict(requestId=str(uuid4()), snapshotId=snapshot, expectedVersion=0, definition=definition))
    body = dict(snapshotId=snapshot, publication=definition['key'], indexCode=index['key']['code'], sourceId=source_id,
                market='DA', fromDate='2021-03-14', throughDate='2021-03-14', knownAt=first['knownAt'])
    before = query('SELECT (SELECT count(*) FROM power_data.prices),(SELECT count(*) FROM market_data.observations),'
                   '(SELECT count(*) FROM market_data.price_publication_versions),(SELECT count(*) FROM market_data.price_publication_releases);')

    def check(request, prices, receipt):
        result = client.call('price-publications/power/preview', request)
        assert result['publication'] == definition['key'] and result['index'] == index['key']
        assert result['market'] == 'DA' and result['source']['id'] == source_id
        assert result['publicationStatus'] == 'collected' and result['contractuallyFinal'] is False
        assert result['bindingSaved'] is False and result['complete'] is True
        assert result['expectedIntervals'] == 23 and result['missingIntervals'] == 0
        assert instant(result['knownAt']) == instant(request['knownAt']) and result['contextId']
        assert [r['price']['value'] for r in result['rows']] == prices
        assert all(r['batchId'] == receipt['batchId'] and r['sourceId'] == source_id for r in result['rows'])
        assert instant(result['rows'][0]['start']) == instant('2021-03-14T05:00:00Z')
        assert instant(result['rows'][-1]['end']) == instant('2021-03-15T04:00:00Z')
        return result

    check(body, first_prices, first)
    check(body | {'knownAt': second['knownAt']}, second_prices, second)
    check(body, first_prices, first)
    client.call('profile', {'profile': 'viewer'})
    check(body, first_prices, first)
    # Publication metadata retains the catalogue's exact current reference source.
    incompatible=deepcopy(definition);incompatible['key']['id']=str(uuid4())
    incompatible['references']['revisionId']=str(uuid4())
    client.call('profile',{'profile':'editor'})
    assert client.call('price-publications/save',dict(requestId=str(uuid4()),snapshotId=snapshot,expectedVersion=0,definition=incompatible),expected=422)['code']=='publication_definition'
    client.call('profile', {'profile': 'viewer'})
    empty = client.call('price-publications/power/preview', body | {'fromDate': '2021-03-15', 'throughDate': '2021-03-15'})
    assert empty['complete'] is False and empty['expectedIntervals'] == 24 and empty['missingIntervals'] == 24
    assert empty['rows'] == [] and empty['contractuallyFinal'] is False
    for change in [dict(market='RT'), dict(market=''), dict(indexCode='missing'),
                   dict(sourceId=manifest['powerActual']['ercot']['sourceId']), dict(throughDate='2021-04-14'),
                   dict(fromDate='9999-12-31', throughDate='9999-12-31'),
                   dict(knownAt='2999-01-01T00:00:00Z')]:
        client.call('price-publications/power/preview', body | change, expected=422)
    client.call('price-publications/power/preview', body | {'sourceId': str(uuid4())}, expected=403)
    client.call('profile', {'profile': 'editor'})
    for member, clock in [(index, next(c['reference'] for c in catalog['clocks'] if c['reference']['code'] != source['clock_code'])),
                          (next(i for i in catalog['indices'] if i['key']['code'] == 'test_power_scaled_index'), definition['clock'])]:
        incompatible = deepcopy(definition)
        incompatible['key']['id'] = str(uuid4())
        incompatible['indices'] = [{k: v for k, v in member.items() if k != 'name'} | dict(symbol='CAPTURED_MISMATCH', statistic='hourly_total_lmp')]
        incompatible['clock'] = clock
        client.call('price-publications/save', dict(requestId=str(uuid4()), snapshotId=snapshot, expectedVersion=0, definition=incompatible))
        assert client.call('price-publications/power/preview', body | {'publication': incompatible['key'], 'indexCode': member['key']['code']}, expected=422)['code'] == 'publication_power_source'
    client.call('profile', {'profile': 'viewer'})
    missing = deepcopy(body); missing['publication']['version'] = 999
    client.call('price-publications/power/preview', missing, expected=422)
    for key in ['market', 'knownAt', 'publication', 'sourceId']:
        omitted = deepcopy(body); del omitted[key]
        client.call('price-publications/power/preview', omitted, expected=400)
    after = query('SELECT (SELECT count(*) FROM power_data.prices),(SELECT count(*) FROM market_data.observations),'
                  '(SELECT count(*) FROM market_data.price_publication_versions),(SELECT count(*) FROM market_data.price_publication_releases);')
    before_counts, after_counts = before.split('|'), after.split('|')
    assert after_counts[:2] == before_counts[:2] and after_counts[3] == before_counts[3]
    assert int(after_counts[2]) == int(before_counts[2]) + 2, 'Only explicit test publication saves may add metadata.'
    print('PASS captured publication inspection: exact source/index, real 23-hour grid, immutable cutoffs, missing coverage and no financial writes', flush=True)
    return body
