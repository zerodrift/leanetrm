"""Independent selected-position pricing in an owned synthetic preview."""
import json
from pathlib import Path
from uuid import UUID, uuid4


def run_checks(client, context, view, expected, query, earlier, owned_root):
    root = Path(owned_root)
    assert (root / '.openetrm-preview').is_file() and (root / 'data/postmaster.pid').is_file()
    facts = client.call('trading/positions', dict(context=context, bookIds=view['bookIds'],
                       portfolioIds=view['portfolioIds'], offset=0, limit=64))
    selected = {**context, 'knowledgeMode': 'retained', 'contextId': facts['context']['contextId']}

    def request(row):
        return dict(context=selected, tradeId=row['tradeId'], bindingId=row['bindingId'],
                    revision=int(row['revision']), instrumentType=row['instrumentType'])

    representatives = [next(row for row in expected if row['instrumentType'] == kind)
                       for kind in ('future', 'financial_swap', 'heat_rate', 'ppa')]
    for row in representatives:
        actual = client.call('trading/position', request(row))
        assert actual['context'] == facts['context'] and actual['asOf'] == facts['asOf']
        assert actual['holding'] == row, (row['instrumentType'], actual['holding'], row)
    print('PASS individual positions: exact existing product rows and formats under one retained context', flush=True)

    historical = client.call('trading/position', {**request(representatives[0]), 'context': earlier}, expected=422)
    assert historical['code'] == 'position_unavailable', historical
    print('PASS individual positions: an exact earlier context cannot substitute later bookings', flush=True)

    target = representatives[-1]
    for change, code in [({'revision': int(target['revision']) + 1}, 'position_revision'),
                         ({'bindingId': str(uuid4())}, 'position_unavailable'),
                         ({'instrumentType': 'future'}, 'position_unavailable')]:
        body = request(target)
        body.update(change)
        assert client.call('trading/position', body, expected=422)['code'] == code
    print('PASS individual positions: stale revision, wrong binding and wrong product never select another position', flush=True)

    # Deliberately remove only another instrument's retained mapping in this
    # owned disposable database. The report names the whole affected trade as
    # needing rebooking; strict PPA pricing still refuses. Selecting a healthy
    # trade must not load that unrelated instrument at all.
    unrelated = next(row for row in expected if row['instrumentType'] == 'ppa'
                     and row['contractId'] != target['contractId'])
    instrument = unrelated['contractId']
    assert str(UUID(instrument)) == instrument
    mappings = query(f"SELECT coalesce(jsonb_agg(to_jsonb(m)), '[]'::jsonb)::text FROM trading.instrument_side_schedules m WHERE instrument_id='{instrument}'::uuid;")
    query(f"""ALTER TABLE trading.instrument_side_schedules DISABLE TRIGGER USER;
      DELETE FROM trading.instrument_side_schedules WHERE instrument_id='{instrument}'::uuid;
      ALTER TABLE trading.instrument_side_schedules ENABLE TRIGGER USER;""")
    def strict(code):
        catalog = client.call('ppa/catalog', {'snapshotId': context['snapshotId']})
        body = dict(snapshotId=context['snapshotId'], definitionRevisionId=catalog['definitions']['revisionId'],
                    referenceCatalogId=catalog['reference']['catalogId'], referenceRevisionId=catalog['reference']['revisionId'],
                    scope='trades', selectedIds=[unrelated['tradeId']], explicitSubset=True, terms=None, quantity=None,
                    coverageFrom=None, coverageTo=None, knownAt=catalog['knownAt'], pendingActuals='estimate_until_actual_v1', detailMonths=[])
        assert client.call('ppa/preview', body, expected=422)['code'] == code
        client.call('profile', {'profile': 'risk'})
        try:
            risk = client.call('portfolio/risk/catalog', dict(context=selected, bookIds=view['bookIds'], portfolioIds=[]), expected=422)
            assert risk['code'] == 'pilot_close_inputs' and risk['affectedBindings'], risk
            assert {binding['reason'] for binding in risk['affectedBindings']} == {code}, risk
        finally:
            client.call('profile', {'profile': 'trader'})

    def report(code, affected):
        summary = dict(mode='pivot', bookIds=view['bookIds'], portfolioIds=[], groupBy=['instrumentType'],
                       measures=['presentValue', 'executionPnl', 'tradeCount'], pivotConvention='trading_blotter_money_totals_v1')
        read = client.call('trading/workspace', dict(context=selected, view=view, bookingCatalog='directory', summaries=[summary]))
        actual = read['blotter']['holdings']
        assert len(actual) == len(expected)
        bad = [row for row in actual if row['tradeId'] in affected]
        assert bad and all(row['unavailableReason'] == 'needs_rebooking' and row['unavailableCodes'] == [code] for row in bad), bad
        assert all(row[field] is None for row in bad for field in ('presentValue', 'completePv', 'overdueKnown', 'overdueEstimated'))
        assert [row for row in actual if row['tradeId'] not in affected] == [row for row in expected if row['tradeId'] not in affected]
        group = next(g for g in read['summaries'][0]['groups'] if g['instrumentType'] == 'ppa')
        assert group['presentValue'] is None and group['valuationUnavailableReason'] == 'needs_rebooking', group
        assert all(g.get('valuationUnavailableReason') is None for g in read['summaries'][0]['groups'] if g['instrumentType'] != 'ppa')
        strict(code)

    try:
        report('side_schedule_missing', {row['tradeId'] for row in expected if row['contractId'] == instrument})
        actual = client.call('trading/position', request(target))
        assert actual['holding'] == target
    finally:
        literal = mappings.replace("'", "''")
        query(f"""ALTER TABLE trading.instrument_side_schedules DISABLE TRIGGER USER;
          INSERT INTO trading.instrument_side_schedules SELECT * FROM jsonb_populate_recordset(NULL::trading.instrument_side_schedules,'{literal}'::jsonb);
          ALTER TABLE trading.instrument_side_schedules ENABLE TRIGGER USER;""")
    print('PASS PPA report: missing retained schedule isolates whole trades, preserves exact healthy values, and leaves complete PPA totals unavailable', flush=True)
    print('PASS individual positions: another PPA missing its retained schedule cannot block the selected PPA', flush=True)

    # Unsupported storage is a rebooking refusal. A supported-format artifact
    # with corrupt content remains a hard failure, even in the report path.
    schedule = json.loads(mappings)[0]['schedule_id']
    assert str(UUID(schedule)) == schedule
    header = json.loads(query(f"SELECT json_build_object('version',schema_version,'hash',encode(content_hash,'hex')) FROM trading.side_schedules WHERE id='{schedule}';"))
    affected_instruments = set(json.loads(query(f"SELECT json_agg(DISTINCT instrument_id) FROM trading.instrument_side_schedules WHERE schedule_id='{schedule}';")))
    affected = {row['tradeId'] for row in expected if row['contractId'] in affected_instruments}
    def change(sql):
        query('ALTER TABLE trading.side_schedules DISABLE TRIGGER USER;' + sql + 'ALTER TABLE trading.side_schedules ENABLE TRIGGER USER;')
    try:
        change(f"UPDATE trading.side_schedules SET schema_version='synthetic_unsupported_format' WHERE id='{schedule}';")
        report('side_schedule_unsupported', affected)
        change(f"UPDATE trading.side_schedules SET schema_version='{header['version']}',content_hash=decode(repeat('ab',32),'hex') WHERE id='{schedule}';")
        failed = client.call('trading/workspace', dict(context=selected, view=view, bookingCatalog='directory', summaries=[]), expected=422)
        assert failed['code'] == 'pilot_close_inputs' and {b['reason'] for b in failed['affectedBindings']} == {'side_schedule_definition'}, failed
        strict('side_schedule_definition')
        client.call('profile', {'profile': 'admin'})
        try:
            client.call('trading/workspace', dict(context=selected, view=view, bookingCatalog='directory', summaries=[]), expected=403)
        finally:
            client.call('profile', {'profile': 'trader'})
    finally:
        change(f"UPDATE trading.side_schedules SET schema_version='{header['version']}',content_hash=decode('{header['hash']}','hex') WHERE id='{schedule}';")
    restored = client.call('trading/workspace', dict(context=selected, view=view, bookingCatalog='directory', summaries=[]))
    assert restored['blotter']['holdings'] == expected
    print('PASS PPA report: unsupported format needs rebooking; corrupt manifests and current authority stay hard; another read reloads restored content', flush=True)

    client.call('profile', {'profile': 'admin'})
    try:
        client.call('trading/position', request(target), expected=403)
    finally:
        client.call('profile', {'profile': 'trader'})
    print('PASS individual positions: current trading authority is required before frame or trade selection', flush=True)
