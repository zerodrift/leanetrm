"""Product selection over captured prices, using real 2020 PJM dates and masks."""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
from uuid import uuid4

from exact_values import fraction, instant


def run_checks(client, query, manifest, capture, collector):
    publish = collector.publish
    client.call('profile', {'profile': 'editor'})
    catalog = client.call('price-publications?snapshotId=' + capture['snapshotId'])
    original = next(p for p in catalog['publications'] if p['key'] == capture['publication'])
    index = next(i for i in catalog['indices'] if i['key']['code'] == 'pjm_2020_captured_index' and i['quote'] == original['indices'][0]['quote'])
    definition = deepcopy(original)
    definition['key']['id'] = str(uuid4())
    definition['name'] = 'PJM 2020 captured product check'
    definition['calendar'] = next(c['reference'] for c in catalog['calendars'] if c['reference']['code'] == 'pjm_2020_nerc')
    definition['indices'] = [{k: v for k, v in index.items() if k != 'name'} | dict(symbol='CAPTURED_2020', statistic='hourly_total_lmp')]
    client.call('price-publications/save', dict(requestId=str(uuid4()), snapshotId=capture['snapshotId'], expectedVersion=0, definition=definition))
    publish('pjm', '2020-12-18', datetime(2020, 12, 18, 5, tzinfo=timezone.utc), 24, 60, ['20'] * 24, 1)
    publish('pjm', '2020-12-19', datetime(2020, 12, 19, 5, tzinfo=timezone.utc), 24, 60, ['40'] * 24, 1)
    christmas = publish('pjm', '2020-12-25', datetime(2020, 12, 25, 5, tzinfo=timezone.utc), 24, 60, ['-5'] * 24, 1)
    scoped = capture | dict(publication=definition['key'], indexCode=index['key']['code'], fromDate='2020-12-18', throughDate='2020-12-19', knownAt=christmas['knownAt'])
    products = client.call('price-publications/power/products?snapshotId=' + capture['snapshotId'])
    pjm = next(p for p in products['products'] if p['code'] == 'pjm')
    request = dict(capture=scoped, product=pjm['code'], productVersion=pjm['version'], block='off', averaging='duration_weighted_price_mean_v1')
    before = query('SELECT (SELECT count(*) FROM power_data.prices),(SELECT count(*) FROM market_data.observations),(SELECT count(*) FROM market_data.price_publication_releases);')
    client.call('profile', {'profile': 'viewer'})
    response = client.call('price-publications/power/product-preview', request)
    assert response['expectedIntervals'] == 32 and response['missingIntervals'] == 0 and response['complete']
    assert fraction(response['average']) == 35 and [fraction(d['average']) for d in response['days']] == [20, 40]
    assert response['capture']['contractuallyFinal'] is False and response['calendar']['code'] == 'pjm_2020_nerc'
    assert response['calendar']['holidays'] == []
    assert response['product']['code'] == 'pjm' and response['product']['version'] == pjm['version']
    expected = [datetime(2020, 12, 18, 5, tzinfo=timezone.utc) + timedelta(hours=n) for n in range(7)]
    expected += [datetime(2020, 12, 19, 4, tzinfo=timezone.utc) + timedelta(hours=n) for n in range(25)]
    assert [instant(at) for at in response['selectedStarts']] == expected
    christmas_request = deepcopy(request)
    christmas_request['capture'].update(fromDate='2020-12-25', throughDate='2020-12-25')
    holiday = client.call('price-publications/power/product-preview', christmas_request)
    assert holiday['expectedIntervals'] == 24 and fraction(holiday['average']) == -5
    assert holiday['calendar']['holidays'] == ['2020-12-25']
    no_peak = client.call('price-publications/power/product-preview', christmas_request | {'block': 'peak'})
    assert no_peak['expectedIntervals'] == 0 and no_peak['average'] is None and no_peak['state'] == 'outside_product'
    incomplete = client.call('price-publications/power/product-preview', request | {'capture': scoped | {'throughDate': '2020-12-20'}})
    assert incomplete['missingIntervals'] == 24 and incomplete['average'] is None and not incomplete['complete']
    changed = publish('pjm', '2020-12-18', datetime(2020, 12, 18, 5, tzinfo=timezone.utc), 24, 60, ['24'] * 24, 2)
    assert fraction(client.call('price-publications/power/product-preview', request)['average']) == 35
    assert fraction(client.call('price-publications/power/product-preview', request | {'capture': scoped | {'knownAt': changed['knownAt']}})['average']) == 36
    for change in [dict(averaging='invented'), dict(block='invented'), dict(product='missing')]:
        client.call('price-publications/power/product-preview', request | change, expected=422)
    client.call('price-publications/power/product-preview', request | {'productVersion': 999}, expected=409)
    client.call('price-publications/power/product-preview', request | {'capture': scoped | {'fromDate': '2021-01-01', 'throughDate': '2021-01-01'}}, expected=422)
    for key in ['capture', 'product', 'productVersion', 'block', 'averaging']:
        omitted = deepcopy(request); del omitted[key]
        client.call('price-publications/power/product-preview', omitted, expected=400)
    client.call('price-publications/power/product-preview', request | {'capture': None}, expected=422)
    # Intentionally incomplete import: the selected Monday peak is complete,
    # but its eight excluded hours are absent. This is selected-product coverage,
    # not the separate delivery-cash consumer's full-native-day admission.
    batch = str(uuid4())
    start = datetime(2020, 12, 21, 12, tzinfo=timezone.utc)
    values = ','.join(f"('{batch}','{scoped['sourceId']}','{(start + timedelta(hours=n)).isoformat()}',"
                      f"'{(start + timedelta(hours=n + 1)).isoformat()}',0,1,'{manifest['catalogId']}')" for n in range(16))
    query(f"""BEGIN;
      INSERT INTO power_data.attempts(id,job_id,generation,worker_pid,security_generation,from_date,through_date,started_at,state,finished_at,error_code,catalog_id)
      SELECT '{batch}',id,generation,pg_backend_pid(),(SELECT generation FROM security.epoch),'2020-12-21','2020-12-21',clock_timestamp(),'succeeded',clock_timestamp(),NULL,catalog_id
      FROM power_data.jobs WHERE id='{collector.jobs['pjm']}';
      INSERT INTO power_data.batches(id,catalog_id,publication_xid) VALUES('{batch}','{manifest['catalogId']}',pg_current_xact_id());
      INSERT INTO power_data.prices(batch_id,source_id,starts_at,ends_at,price,provider_revision,catalog_id) VALUES {values}; COMMIT;
      SELECT power_data.certify('{manifest['catalogId']}');""")
    cutoff = query(f"SELECT to_char(r.known_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM power_data.batches b JOIN power_data.receipts r USING(sequence) WHERE b.id='{batch}';")
    partial = request | {'block': 'peak', 'capture': scoped | {'fromDate': '2020-12-21', 'throughDate': '2020-12-21', 'knownAt': cutoff}}
    selected = client.call('price-publications/power/product-preview', partial)
    assert selected['complete'] and selected['expectedIntervals'] == 16 and fraction(selected['average']) == 0
    assert not selected['capture']['complete'] and selected['capture']['missingIntervals'] == 8
    excluded = client.call('price-publications/power/product-preview', partial | {'block': 'off'})
    assert excluded['missingIntervals'] == 8 and excluded['average'] is None
    # Only explicit correction/import writes above add rows; inspection adds none.
    after = query('SELECT (SELECT count(*) FROM power_data.prices),(SELECT count(*) FROM market_data.observations),(SELECT count(*) FROM market_data.price_publication_releases);')
    a, b = before.split('|'), after.split('|')
    assert int(b[0]) == int(a[0]) + 40 and b[1:] == a[1:]
    print('PASS product HTTP: sourced 2020 PJM hours/holidays, exact means, coverage, negative prices and retained corrections', flush=True)
    return request
