"""Independent selected-position pricing in an owned synthetic preview."""
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
    # owned disposable database. The normal complete-book read must refuse;
    # selecting this trade must not load that unrelated instrument at all.
    unrelated = next(row for row in expected if row['instrumentType'] == 'ppa'
                     and row['contractId'] != target['contractId'])
    instrument = unrelated['contractId']
    assert str(UUID(instrument)) == instrument
    mappings = query(f"SELECT coalesce(jsonb_agg(to_jsonb(m)), '[]'::jsonb)::text FROM trading.instrument_side_schedules m WHERE instrument_id='{instrument}'::uuid;")
    query(f"""ALTER TABLE trading.instrument_side_schedules DISABLE TRIGGER USER;
      DELETE FROM trading.instrument_side_schedules WHERE instrument_id='{instrument}'::uuid;
      ALTER TABLE trading.instrument_side_schedules ENABLE TRIGGER USER;""")
    try:
        failed = client.call('trading/workspace', dict(context=selected, view=view,
                            bookingCatalog='directory', summaries=[]), expected=422)
        assert failed['code'] == 'side_schedule_missing', failed
        actual = client.call('trading/position', request(target))
        assert actual['holding'] == target
    finally:
        literal = mappings.replace("'", "''")
        query(f"""ALTER TABLE trading.instrument_side_schedules DISABLE TRIGGER USER;
          INSERT INTO trading.instrument_side_schedules SELECT * FROM jsonb_populate_recordset(NULL::trading.instrument_side_schedules,'{literal}'::jsonb);
          ALTER TABLE trading.instrument_side_schedules ENABLE TRIGGER USER;""")
    print('PASS individual positions: another PPA missing its retained schedule cannot block the selected PPA', flush=True)

    client.call('profile', {'profile': 'admin'})
    try:
        client.call('trading/position', request(target), expected=403)
    finally:
        client.call('profile', {'profile': 'trader'})
    print('PASS individual positions: current trading authority is required before frame or trade selection', flush=True)
