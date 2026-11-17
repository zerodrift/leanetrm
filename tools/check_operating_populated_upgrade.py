"""Replay the operating upgrade over real bookings in a caller-owned disposable fixture."""
import json
from pathlib import Path
import re
from uuid import UUID

from assistant_context import migration_body


TABLES = (
    'trades', 'instruments', 'publications', 'power_operating_instruments', 'operating_memberships',
    'operating_trade_revisions', 'operating_plan_quantities', 'operating_side_prices',
    'operating_booking_requests', 'sides', 'side_schedules', 'side_schedule_rows',
    'side_schedule_grids', 'side_schedule_edges', 'side_schedule_bindings',
    'side_schedule_roots', 'side_schedule_ports', 'side_schedule_authorities',
    'side_schedule_output_sets', 'side_schedule_output_members', 'side_schedule_programs',
    'side_schedule_seals', 'side_schedule_publications', 'instrument_side_schedules',
    'instrument_schedule_compositions', 'side_schedule_requests', 'side_schedule_request_items',
    'side_schedule_request_inputs', 'side_schedule_request_seals', 'side_schedule_chunks',
)


def literal(value):
    return "'" + value.replace("'", "''") + "'"


def history_sql(catalog):
    values = []
    for table in TABLES:
        rows = (f"SELECT count(*) AS count,encode(sha256(convert_to(coalesce("
                f"jsonb_agg(to_jsonb(r) ORDER BY to_jsonb(r)::text),'[]'::jsonb)::text,'UTF8')),'hex') AS digest "
                f"FROM trading.{table} r WHERE catalog_id='{catalog}'")
        values.extend((literal(table), f'(SELECT to_jsonb(s) FROM({rows})s)'))
    return 'SELECT jsonb_build_object(' + ','.join(values) + ')::text;'


def original_function(source, name):
    matches = re.findall(r'CREATE FUNCTION ' + re.escape(name) + r'\(.*?\nEND\$\$;', source, re.S)
    if len(matches) != 1:
        raise AssertionError('Expected one exact tracked predecessor: ' + name)
    return matches[0].replace('CREATE FUNCTION ', 'CREATE OR REPLACE FUNCTION ', 1)


def run_checks(query, manifest, positions):
    """positions contain the actual legacy battery, thermal and solar bookings."""
    root = Path(__file__).resolve().parents[1]
    catalog = str(UUID(manifest['catalogId']))
    assert {row['kind'] for row in positions} == {'battery', 'thermal', 'solar'}
    assert all(row['request']['plan']['valuation'] is not None and
               'composition' not in row['request']['plan'] for row in positions)
    old = (root / 'db/migrations/0345_operating_right_bookings.sql').read_text()
    migration = migration_body((root / 'db/migrations/0355_operating_side_composition.sql').read_text())
    calendar = query("SELECT pg_get_functiondef('trading.request_side_calendar_policy(jsonb,integer)'::regprocedure);")
    added = " IF command->>'convention'='supplied_operating_right_trade_v1' AND command->'plan' ? 'composition' THEN"
    original = " IF command->>'convention'='supplied_operating_right_trade_v1' THEN"
    assert calendar.count(added) == 1 and calendar.count(original) == 1
    start, end = calendar.index(added), calendar.index(original)
    assert start < end
    restore = '\n'.join(original_function(old, name) for name in (
        'trading.operating_plan_admission', 'trading.require_operating_program_shape',
        'security.book_operating_right')) + '\n' + calendar[:start] + calendar[end:] + ';\n'
    restore += """
ALTER TABLE trading.operating_memberships DROP CONSTRAINT operating_memberships_name_check,
 ADD CONSTRAINT operating_memberships_name_check CHECK(name IN('discharging_energy','energy_output','payment_energy','charging_energy','fuel_input','variable_fee','access_fee')),
 ADD CONSTRAINT operating_memberships_catalog_id_instrument_id_name_key UNIQUE(catalog_id,instrument_id,name);
DROP FUNCTION trading.operating_source_port(text),trading.require_operating_composition(jsonb,text),trading.operating_side_name_valid(text);
"""
    # The candidate's complete predecessor guard checks definitions, owners,
    # ACLs and execution attributes. OIDs are deliberately not comparison keys.
    proof = f"""DO $proof$ DECLARE member record;BEGIN
 FOR member IN SELECT m.instrument_id,m.slot,s.schedule_id FROM trading.operating_memberships m
 JOIN trading.instrument_side_schedules s USING(catalog_id,instrument_id,slot)
 WHERE m.catalog_id='{catalog}' LOOP
  PERFORM trading.require_operating_program_shape('{catalog}',member.schedule_id,member.slot,member.instrument_id);
 END LOOP;
END$proof$;"""
    quantities = ''
    for row in positions:
        instrument = str(UUID(row['saved']['instrumentId']))
        plan = literal(json.dumps(row['request']['plan'])) + '::jsonb'
        quantities += f"""DO $quantities$ DECLARE f physical.power_operating_facility_versions;actual jsonb;stored jsonb;BEGIN
 SELECT v.* INTO STRICT f FROM trading.power_operating_instruments h
 JOIN physical.power_operating_facility_versions v ON(v.catalog_id,v.facility_id,v.version)=(h.catalog_id,h.facility_id,h.facility_version)
 WHERE(h.catalog_id,h.instrument_id)=('{catalog}','{instrument}');
 SELECT jsonb_agg(value ORDER BY(value->>'ordinal')::integer,value->>'name') INTO actual
 FROM jsonb_array_elements(trading.operating_plan_admission(f,{plan},'2020-12-15T12:00:00Z'));
 SELECT jsonb_agg(jsonb_build_object('ordinal',ordinal,'name',name,'from',interval_from,'to',interval_to,
  'quantity',quantity_code,'numerator',numerator::text,'denominator',denominator::text) ORDER BY ordinal,name)
 INTO stored FROM trading.operating_plan_quantities WHERE(catalog_id,instrument_id)=('{catalog}','{instrument}');
 IF actual IS DISTINCT FROM stored THEN RAISE EXCEPTION 'operating_upgrade_predecessor_quantities';END IF;
END$quantities$;"""
    before = json.loads(query(history_sql(catalog)))
    assert before['power_operating_instruments']['count'] == 3
    assert before['side_schedule_programs']['count'] >= 5

    def reject(sql, expected):
        try:
            query(sql)
        except RuntimeError as error:
            assert expected in str(error), str(error)
        else:
            raise AssertionError('Populated upgrade probe accepted ' + expected)
        assert json.loads(query(history_sql(catalog))) == before

    reject('BEGIN;' + restore + proof +
           "ALTER FUNCTION security.book_operating_right(uuid,jsonb,jsonb,timestamp with time zone) SET work_mem='64kB';" +
           migration + 'COMMIT;', 'operating_composition_upgrade: unexpected helper')
    thermal = next(row for row in positions if row['kind'] == 'thermal')
    instrument = str(UUID(thermal['saved']['instrumentId']))
    # This transaction deliberately corrupts only its own synthetic fixture.
    # The predecessor admission proof must notice a changed fuel fact; a rejected
    # psql transaction rolls the corruption and trigger state back together.
    reject('BEGIN;' + restore +
           "ALTER TABLE trading.operating_plan_quantities DISABLE TRIGGER USER;" +
           f"UPDATE trading.operating_plan_quantities SET numerator=numerator+denominator WHERE(catalog_id,instrument_id,name)=('{catalog}','{instrument}','fuel');" +
           proof + 'COMMIT;', 'operating_schedule_quantity')

    token = str(UUID(query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
        f"'https://preview.invalid','trader','human_interactive','{manifest['tenantId']}',"
        f"'{manifest['workspaceId']}',NULL,'preview_runtime',120);")))
    replay = 'SET LOCAL SESSION AUTHORIZATION preview_runtime;' + f"SELECT security.use_context('{token}');"
    for row in positions:
        body = literal(json.dumps(row['request'])) + '::jsonb'
        prepared = literal(json.dumps(dict(instrumentId=row['saved']['instrumentId'], members=[]))) + '::jsonb'
        expected = literal(json.dumps(row['saved'])) + '::jsonb'
        replay += f"""DO $replay$ DECLARE result jsonb;BEGIN
 result=security.book_operating_right('{catalog}',{body},{prepared},'2020-12-15T12:00:00Z');
 IF result IS DISTINCT FROM {expected} THEN RAISE EXCEPTION 'operating_upgrade_replay';END IF;
END$replay$;"""
    replay += 'RESET SESSION AUTHORIZATION;'
    expected = literal(json.dumps(before)) + '::jsonb'
    snapshot = history_sql(catalog).removeprefix('SELECT ').removesuffix('::text;')
    verify = f"""DO $unchanged$ BEGIN
 IF ({snapshot}) IS DISTINCT FROM {expected} THEN RAISE EXCEPTION 'operating_upgrade_history_changed';END IF;
END$unchanged$;"""
    # Commit the actual candidate upgrade. Subsequent HTTP get/value/replay calls
    # exercise this installed result, rather than a rolled-back original schema.
    query('BEGIN;' + restore + quantities + proof + replay + migration + verify + 'COMMIT;')
    assert json.loads(query(history_sql(catalog))) == before
    print(f'PASS populated operating upgrade: exact predecessor reproduces battery/thermal/solar quantities and admits retained programs; drift and fuel corruption refuse; committed upgrade preserves {len(TABLES)} history/program/relationship sets and original outcomes', flush=True)
