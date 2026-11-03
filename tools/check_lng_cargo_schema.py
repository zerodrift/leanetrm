"""Behavior checks for the owned LNG cargo fixture and additive migration."""
import json
import subprocess
import time
from uuid import uuid4

from check_booked_side_programs import capture_context, runtime_query
from lng_fixture import REVISION


def physical_selection(manifest):
    return {'referenceCatalogId':manifest['referenceCatalogId'], 'referenceRevisionId':manifest['referenceRevisionId'],
            'commodity':'synthetic_lng', 'product':'synthetic_lng_product',
            'loadFacility':'synthetic_lng_load_terminal', 'loadCustody':'synthetic_lng_load_custody',
            'dischargeFacility':'synthetic_lng_discharge_terminal', 'dischargeCustody':'synthetic_lng_discharge_custody',
            'energyQuantityCode':'synthetic_lng_energy', 'clock':'test_utc'}


def run_schema_checks(query):
    required = ['lng_products', 'lng_custody_points', 'lng_mass_energy_conversions',
                'incoterms_editions', 'incoterms_rules']
    for table in required:
        assert query(f"SELECT to_regclass('ref_data.{table}') IS NOT NULL;") == 't', table
    assert query("SELECT to_regprocedure('security.book_physical_lng(uuid,jsonb,timestamp with time zone)') IS NOT NULL;") == 't'
    assert query("SELECT assistant.is_prompt_page('blotter_new_lng');") == 't'
    for table in required:
        assert query(f"SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='ref_data.{table}'::regclass;") == 't', table
    print('PASS LNG schema: typed reference and booking consumers with forced row security', flush=True)


def run_resolution_checks(query, manifest):
    catalog = manifest['catalogId']
    context = capture_context(query, manifest, '2020-12-18')
    bind = f"SELECT security.use_market_context('{catalog}','{context}');"
    physical = physical_selection(manifest)

    def expression(selected, start='2021-01-14T00:00:00Z', end='2021-01-15T00:00:00Z'):
        encoded = json.dumps(selected).replace("'", "''")
        return f"security.resolve_lng_delivery('{catalog}','{encoded}'::jsonb,'{start}','{end}','2020-12-18T16:00:00Z')"

    selected = json.loads(runtime_query(query, manifest, bind + 'SELECT ' + expression(physical) + ';'))
    assert selected == {'definitionRevisionId':REVISION, 'assignmentCode':'synthetic_lng_assignment', 'curve':'synthetic_lng_forward'}, selected
    cases = [({**physical, 'product':'synthetic_lng_unpriced'}, 'lng_price_mapping_missing', None),
             ({**physical, 'loadCustody':physical['dischargeCustody']}, 'lng_reference_relationship', None),
             ({**physical, 'curve':'synthetic_lng_forward'}, 'lng_reference_input', None),
             (physical, 'lng_price_mapping_coverage', '2021-02-01T00:00:00.000001Z')]
    for value, code, end in cases:
        call = expression(value, end=end) if end else expression(value)
        statement = bind + f"""DO $case$ BEGIN
          PERFORM {call}; RAISE EXCEPTION 'expected LNG refusal was absent';
          EXCEPTION WHEN raise_exception THEN IF SQLERRM<>'{code}' THEN RAISE; END IF;
        END$case$;"""
        runtime_query(query, manifest, statement)
    print('PASS LNG resolution: exact physical mapping, unpriced masters, independent load/discharge custody, no curve hints and full coverage', flush=True)


def run_booking_authority_check(query, manifest, psql, environment, receipt):
    """A completed command still needs booking authority after publication waits."""
    catalog=manifest['catalogId']
    body=query(f"SELECT request FROM trading.trades WHERE(catalog_id,id)=('{catalog}','{receipt['tradeId']}');")
    principal=query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='trader';")
    permissions=query(f"SELECT jsonb_agg(to_jsonb(p)) FROM security.role_permissions p WHERE permission='BookTrade' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');")
    role,grant,app=str(uuid4()),str(uuid4()),'synthetic-lng-expiry-'+str(uuid4())
    locker=reader=None
    try:
        query(f"""BEGIN;INSERT INTO security.roles VALUES('{role}','{app}','Synthetic expiring LNG booker');
          INSERT INTO security.role_permissions VALUES('{role}','BookTrade');
          DELETE FROM security.role_permissions WHERE permission='BookTrade' AND role_id IN(SELECT role_id FROM security.role_grants WHERE principal_id='{principal}');
          INSERT INTO security.role_grants VALUES('{grant}','{principal}','{role}','{manifest['tenantId']}','{manifest['workspaceId']}',clock_timestamp()-interval '1 day',clock_timestamp()+interval '5 seconds',false);COMMIT;""")
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")
        locker=subprocess.Popen(psql,env=environment,stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        locker.stdin.write('BEGIN;SELECT pg_advisory_xact_lock(730849102);\n\\echo LOCKED\n');locker.stdin.flush()
        while locker.stdout.readline().strip()!='LOCKED':
            if locker.poll() is not None:raise AssertionError(locker.stderr.read())
        reader=subprocess.Popen(psql,env={**environment,'PGAPPNAME':app},stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
        encoded=body.replace("'","''")
        reader.stdin.write(f"\\set VERBOSITY verbose\nBEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');SELECT security.book_physical_lng('{catalog}','{encoded}'::jsonb,'2020-12-18T16:00:00Z');ROLLBACK;\n\\q\n");reader.stdin.flush()
        for _ in range(150):
            if query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{app}' AND wait_event='advisory';")=='1':break
            if reader.poll() is not None:raise AssertionError(reader.communicate())
            time.sleep(.02)
        else:raise AssertionError('The LNG replay did not reach the controlled publication wait')
        for _ in range(150):
            if query(f"SELECT granted_to<=clock_timestamp() FROM security.role_grants WHERE id='{grant}';")=='t':break
            time.sleep(.05)
        else:raise AssertionError('The independent BookTrade grant did not expire')
        locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();_,errors=locker.communicate(timeout=10)
        assert locker.returncode==0,errors
        output,errors=reader.communicate(timeout=10)
        assert reader.returncode!=0 and '42501' in errors,'LNG replay succeeded after booking authority expired: '+output+errors
    finally:
        if locker is not None and locker.poll() is None:
            locker.stdin.write('ROLLBACK;\n\\q\n');locker.stdin.flush();locker.communicate(timeout=10)
        if reader is not None and reader.poll() is None:reader.communicate(timeout=10)
        encoded=permissions.replace("'","''")
        query(f"BEGIN;DELETE FROM security.role_grants WHERE id='{grant}';DELETE FROM security.role_permissions WHERE role_id='{role}';DELETE FROM security.roles WHERE id='{role}';INSERT INTO security.role_permissions SELECT * FROM jsonb_populate_recordset(NULL::security.role_permissions,'{encoded}'::jsonb) ON CONFLICT DO NOTHING;COMMIT;")
    print('PASS native LNG replay: BookTrade expiry during an observed publication lock wait refuses',flush=True)
