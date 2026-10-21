"""Mapped collected-price checks against the launcher's disposable stock database."""
from copy import deepcopy
from datetime import datetime, timedelta, timezone
from decimal import Decimal
from fractions import Fraction
from uuid import uuid4

from exact_values import fraction, instant, rounded
from power_collection_fixture import Collector
from preview_client import PreviewClient


def run_checks(url, query, manifest):
    """Fixture writes are confined to the runner-owned database and synthetic IDs."""
    client = PreviewClient(url)
    client.bootstrap()
    client.call('profile', {'profile': 'viewer'})
    workspace = client.call('workspace')
    frame = workspace['frames'][0]
    catalog = client.call('power/catalog', {'snapshotId': frame['id']})
    c, r, tenant, scope = [manifest[k] for k in ('catalogId', 'referenceCatalogId', 'tenantId', 'workspaceId')]
    config = manifest['powerActual']
    collector = Collector(query, manifest, {key: {**config[key], 'provider': provider} for provider, key in [('PJM', 'pjm'), ('ERCOT', 'ercot')]})
    jobs, publish = collector.jobs, collector.publish

    def request(key, day, start, count, minutes, cutoff, basis='interval_energy', block='all'):
        curve = next(v for v in catalog['curves'] if v['code'] == config[key]['curve'])
        contract = next(v for v in curve['contracts'] if v['from'] <= day < v['to'])
        commodity = curve['commodityCode']
        specification = next(q['specification'] for q in catalog['quantityChoices'] if
            q['commodityCode'] == commodity and q['basis'] ==
            ('total_energy' if basis == 'interval_energy' else 'flat_power'))
        preset = next(p for p in catalog['presets'] if
                      p['product'] == key and p['block'] == block and p['minutes'] == minutes)
        quantities = []
        for n in range(count):
            row = {'start': (start + timedelta(minutes=n * minutes)).isoformat(),
                   'end': (start + timedelta(minutes=(n + 1) * minutes)).isoformat(),
                   'value': str(n % 5 + 1)}
            if basis == 'interval_power':
                row.update(value='2.5', shape='1.2' if n % 2 else '0.8')
            quantities.append(row)
        return {'snapshotId': frame['id'], 'curve': curve['code'], 'contractId': contract['id'],
                'deliveryDate': day, 'knownAt': cutoff, 'product': key, 'productVersion': 1,
                'minutes': minutes, 'nonHolidays': preset['nonHolidays'], 'holidays': preset['holidays'],
                'numericalPolicy': catalog['numericalPolicy'], 'quantityBasis': basis,
                'quantitySpecification': specification, 'quantities': quantities}

    def verify(body, prices, receipt, revision):
        result = client.call('power/settlement', body)
        assert result['saved'] is False and result['publicationStatus'] == 'collected'
        assert result['historyContextId'] and instant(result['knownAt']) == instant(body['knownAt'])
        assert result['sourceId'] == config[body['product']]['sourceId']
        assert result['pins']['definitions']['id'] == manifest['revisionId']
        assert result['pins']['mapping']['source_id'] == result['sourceId']
        inputs = {instant(q['start']): q for q in body['quantities']}
        selected = sorted(inputs)
        assert len(result['rows']) == len(selected)
        expected_cash, expected_energy, price_duration, duration = Fraction(0), Fraction(0), Fraction(0), Fraction(0)
        for row, start, price in zip(result['rows'], selected, prices):
            item = inputs[start]
            hours = Fraction(int((instant(item['end']) - start).total_seconds()), 3600)
            energy = Fraction(item['value'])
            if body['quantityBasis'] == 'interval_power':
                energy *= Fraction(item['shape']) * hours
            contribution = energy * Fraction(price)
            assert instant(row['start']) == start and instant(row['end']) == instant(item['end'])
            assert fraction(row['energy']) == energy and Fraction(row['price']['value']) == Fraction(price)
            assert fraction(row['contribution']) == contribution
            lineage = row['lineage']
            assert lineage['sourceId'] == result['sourceId'] and lineage['batchId'] == receipt['batchId']
            assert instant(lineage['knownAt']) == instant(receipt['knownAt']) and lineage['revision'] == revision
            expected_cash += contribution
            expected_energy += energy
            price_duration += Fraction(price) * hours
            duration += hours
        summary = result['summary']
        assert fraction(result['energy']) == expected_energy
        assert Fraction(int(summary['exactAmountNumerator']), int(summary['exactAmountDenominator'])) == expected_cash
        assert Decimal(summary['amount']) == rounded(expected_cash, 2)
        assert Decimal(summary['hours']) == Decimal(duration.numerator) / Decimal(duration.denominator)
        assert Decimal(summary['averagePrice']) == rounded(price_duration / duration, 18)
        return result

    before = query('SELECT (SELECT count(*) FROM trading.trades),(SELECT count(*) FROM market_data.observations);')
    # Independent UTC oracles for the 2021 Eastern/Central spring transition.
    pjm_start = datetime(2021, 3, 14, 5, tzinfo=timezone.utc)
    first_prices = ['-17.125456789012345678' if n == 0 else str(n - 4) for n in range(23)]
    first = publish('pjm', '2021-03-14', pjm_start, 23, 60, first_prices, 1)
    body = request('pjm', '2021-03-14', pjm_start, 23, 60, first['knownAt'])
    first_result = verify(body, first_prices, first, 1)
    assert first_result['summary']['hours'] == '23'
    power = request('pjm', '2021-03-14', pjm_start, 23, 60, first['knownAt'], 'interval_power')
    power['quantities'][0]['shape'] = '0'
    power['quantities'][1]['value'] = '-2.5'
    power['quantities'].reverse()
    verify(power, first_prices, first, 1)
    print('PASS actual power HTTP: shaped hourly MW and already-shaped MWh retain exact prices, cash and source lineage', flush=True)

    second_prices = [str(2 * n - 9) for n in range(23)]
    second = publish('pjm', '2021-03-14', pjm_start, 23, 60, second_prices, 2)
    assert instant(second['knownAt']) > instant(first['knownAt'])
    verify(body, first_prices, first, 1)
    verify({**body, 'knownAt': second['knownAt']}, second_prices, second, 2)
    print('PASS actual power HTTP: a fixed certified cutoff preserves earlier interval revisions after correction', flush=True)

    ercot_start = datetime(2021, 3, 14, 6, tzinfo=timezone.utc)
    quarter_prices = [str(n % 13 - 5) for n in range(92)]
    quarter = publish('ercot', '2021-03-14', ercot_start, 92, 15, quarter_prices, None)
    verify(request('ercot', '2021-03-14', ercot_start, 92, 15, quarter['knownAt'], 'interval_power'),
           quarter_prices, quarter, None)
    verify(request('ercot', '2021-03-14', ercot_start, 92, 15, quarter['knownAt']),
           quarter_prices, quarter, None)
    print('PASS actual power HTTP: the independently mapped quarter-hour source integrates duration once and preserves absent vendor revisions', flush=True)

    for change in [
        {'quantities': body['quantities'][:-1]},
        {'quantities': body['quantities'] + body['quantities'][:1]},
        {'quantities': [body['quantities'][0]] + body['quantities'][:-1]},
        {'quantities': [{**q, 'shape': '1'} for q in body['quantities']]},
        {'quantityBasis': 'interval_power'},
        {'quantityBasis': 'invented'}, {'minutes': 15}, {'product': 'miso'},
        {'numericalPolicy': 'invented'},
        {'deliveryDate': '2021-04-01'}, {'knownAt': '2999-01-01T00:00:00Z'},
        {'knownAt': (instant(first['knownAt']) - timedelta(microseconds=1)).isoformat()},
        {'nonHolidays': '1'},
    ]:
        client.call('power/settlement', {**body, **change}, expected=422)
    client.call('power/settlement', {**body, 'productVersion': 999}, expected=409)
    shifted = deepcopy(body)
    shifted['quantities'][0]['end'] = (pjm_start + timedelta(minutes=59)).isoformat()
    client.call('power/settlement', shifted, expected=422)
    shifted = deepcopy(body)
    shifted['quantities'][0]['start'] = pjm_start.astimezone(timezone(timedelta(hours=1))).isoformat()
    client.call('power/settlement', shifted, expected=422)
    for missing in [None, '', 'omit']:
        changed = deepcopy(body)
        if missing == 'omit':
            del changed['quantities'][0]['value']
        else:
            changed['quantities'][0]['value'] = missing
        assert client.call('power/settlement', changed, expected=422)['code'] == 'power_quantity'
    missing_shape = deepcopy(power)
    del missing_shape['quantities'][0]['shape']
    assert client.call('power/settlement', missing_shape, expected=422)['code'] == 'power_quantity'
    for key, value in [('sourceId', config['ercot']['sourceId']), ('priceType', 'Official'), ('prices', first_prices)]:
        client.call('power/settlement', {**body, key: value}, expected=400)
    print('PASS actual power HTTP: incomplete quantities, incompatible grid/clock/terms, missing cutoff coverage and caller-supplied prices or official labels reject', flush=True)

    # A trusted import seed intentionally contains only the selected peak rows.
    # The HTTP reader must still reject its missing full native operating day.
    partial_day, partial_start, partial_id = '2021-03-16', datetime(2021, 3, 16, 4, tzinfo=timezone.utc), str(uuid4())
    partial_prices = ','.join(f"('{partial_id}','{config['pjm']['sourceId']}',"
        f"'{(partial_start + timedelta(hours=n)).isoformat()}',"
        f"'{(partial_start + timedelta(hours=n + 1)).isoformat()}',1,1,'{c}')" for n in range(7, 23))
    query(f"""BEGIN;
      INSERT INTO power_data.attempts(id,job_id,generation,worker_pid,security_generation,
        from_date,through_date,started_at,state,finished_at,error_code,catalog_id)
      SELECT '{partial_id}',id,generation,pg_backend_pid(),(SELECT generation FROM security.epoch),
        '{partial_day}','{partial_day}',clock_timestamp(),'succeeded',clock_timestamp(),NULL,catalog_id
      FROM power_data.jobs WHERE id='{jobs['pjm']}';
      INSERT INTO power_data.batches(id,catalog_id,publication_xid) VALUES('{partial_id}','{c}',pg_current_xact_id());
      INSERT INTO power_data.prices(batch_id,source_id,starts_at,ends_at,price,provider_revision,catalog_id)
        VALUES {partial_prices};COMMIT;""")
    query(f"SELECT power_data.certify('{c}');")
    partial_cutoff = query(f"SELECT to_char(r.known_at AT TIME ZONE 'UTC','YYYY-MM-DD\"T\"HH24:MI:SS.US\"Z\"') FROM power_data.batches b JOIN power_data.receipts r USING(sequence) WHERE b.id='{partial_id}';")
    partial = request('pjm', partial_day, partial_start, 24, 60, partial_cutoff, block='peak')
    partial['quantities'] = partial['quantities'][7:23]
    assert client.call('power/settlement', partial, expected=422)['code'] == 'settlement_coverage'
    print('PASS actual power HTTP: full native-day coverage is required even when the selected peak mask itself has complete rows', flush=True)

    anonymous = PreviewClient(url)
    anonymous.bootstrap()
    anonymous.call('power/settlement', body, expected=403)
    client.call('profile', {'profile': 'admin'})
    client.call('power/settlement', body, expected=403)
    client.call('profile', {'profile': 'viewer'})
    access = query(f"SELECT access_mode FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{r}','{tenant}','{scope}');")
    query(f"DELETE FROM security.reference_catalog_scopes WHERE(catalog_id,tenant_id,workspace_id)=('{r}','{tenant}','{scope}');")
    try:
        client.call('power/settlement', body, expected=403)
    finally:
        query(f"INSERT INTO security.reference_catalog_scopes VALUES('{r}','{tenant}','{scope}','{access}');")
    client.call('market/context', {'effectiveOn': '2020-12-18', 'mode': 'current', 'selection': 'latest_correction'})
    client.call('power/settlement', body, expected=422)
    client.call('market/current', {})
    assert query('SELECT (SELECT count(*) FROM trading.trades),(SELECT count(*) FROM market_data.observations);') == before
    print('PASS actual power HTTP: current identity/reference scope and historical rejection apply without booking, fixing publication or payment writes', flush=True)
