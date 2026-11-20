#!/usr/bin/env python3
"""Qualify operating-facility storage in a new disposable stock PostgreSQL cluster."""
import argparse
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import tempfile
import time
from uuid import uuid4

ROOT = Path(__file__).resolve().parents[1]


def run_checks(query, manifest, psql=None, environment=None):
    catalog, definition, reference, revision, tenant, workspace = [manifest[k] for k in
        ('catalogId', 'revisionId', 'referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId')]
    passed = 0

    def check(label, condition=True):
        nonlocal passed
        assert condition, label
        passed += 1
        print('PASS operating facilities: ' + label, flush=True)

    def runtime(sql, actor='trader', expiry=120):
        token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                      f"'https://preview.invalid','{actor}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',{expiry});")
        return ('BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;'
                f"SELECT security.use_context('{token}');" + sql + 'COMMIT;')

    def reject(sql, code, actor='trader'):
        try:
            query(runtime(sql, actor))
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Accepted invalid operating facility: ' + code)

    def literal(value):
        return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"

    thermal = dict(minimumOutputMw='2', maximumOutputMw='10', rampUpMwPerHour='10',
        rampDownMwPerHour='10', startupMaximumOutputMw='5', shutdownMaximumOutputMw='5',
        minimumUpMinutes=60, minimumDownMinutes=60, startupLeadMinutes=30, maximumStarts=4,
        consumptionHeatRate='7', consumptionNoLoadFuelPerHour='1', consumptionStartupFuel='2')
    terms = dict(kind='thermal', definitionCatalogId=catalog, definitionRevisionId=definition,
        referenceCatalogId=reference, referenceRevisionId=revision, facilityCode='test_facility_a',
        clockCode='test_utc', intervalMinutes=60, powerQuantityCode='test_power_megawatt',
        energyQuantityCode='test_power_quantity', fuelQuantityCode='test_gas_quantity',
        battery=None, thermal=thermal, solar=None,
        implementation='structured_power_facility_v1')
    facility = dict(id=str(uuid4()), version=1, effectiveFrom='2020-01-01T00:00:00Z',
                    effectiveTo='2022-01-01T00:00:00Z', terms=terms)

    def save(body=facility, request=None, expected=0):
        return (f"SELECT physical.save_power_operating_facility('{catalog}','{request or uuid4()}',"
                f"{expected},{literal(body)});")

    def capture():
        return query(runtime(f"SELECT security.resolve_operation_context('{catalog}','2020-12-15',NULL);"))

    def read(context, version=1, selected=facility['id'], at='2020-12-15T16:00:00Z', business_on='2020-12-15'):
        return (f"SELECT physical.read_power_operating_facility('{catalog}','{selected}',{version},"
                f"'{context}','{at}','{business_on}');")

    def directory(context, policy):
        return (f"SELECT physical.list_power_operating_facilities('{catalog}','{context}',"
                f"'2020-12-15T16:00:00Z','2020-12-15','{policy}');")

    # Installing capabilities never grants role authority.
    reject(save(), '42501')
    check('installation grants no new Physical authority')
    query("INSERT INTO security.role_permissions SELECT id,p FROM security.roles CROSS JOIN "
          "(VALUES('ReadPhysical'),('ManagePhysicalSetup'))x(p) WHERE code='trader';")
    before = capture()
    request = str(uuid4())
    first = json.loads(query(runtime(save(request=request))))
    check('first authorized save retains the exact physical extension version',
          first['facility']['id'] == facility['id'] and first['facility']['version'] == 1 and first['changed'])
    replay = json.loads(query(runtime(save(request=request))))
    check('same request returns its first outcome exactly', replay == first)
    duplicate = json.loads(query(runtime(save())))
    check('same business fact under a new request creates no second version',
          duplicate['facility'] == first['facility'] and not duplicate['changed'])
    different = json.loads(json.dumps(facility))
    different['terms']['thermal']['maximumOutputMw'] = '12'
    reject(save(different, request), 'power_facility_request_conflict')
    check('request identity cannot be reused for different values')

    initial_context = capture()
    check('certified exact read returns the saved canonical version',
          json.loads(query(runtime(read(initial_context)))) == first['facility'])
    reject(read(before), 'power_facility_unavailable')
    check('a version committed after the retained cutoff stays unavailable')
    reject(read(initial_context, business_on='2020-12-16'), 'power_facility_context')
    reject(read(initial_context, version=99), 'power_facility_unavailable')
    check('exact selection rejects a different effective context date and absent version')

    check('business date remains explicit when the calculation instant crosses UTC midnight',
          json.loads(query(runtime(read(initial_context, at='2020-12-16T01:00:00Z')))) == first['facility'])

    spelling = json.loads(json.dumps(facility))
    spelling['terms']['thermal']['maximumOutputMw'] = '10.0000'
    spelling['effectiveFrom'] = '2020-01-01T00:00:00+00:00'
    same = json.loads(query(runtime(save(spelling))))
    check('exact decimal and UTC spellings share one business fact', not same['changed'] and same['facility'] == first['facility'])

    future = json.loads(json.dumps(different))
    future.update(version=2, effectiveFrom='2021-01-01T00:00:00Z')
    second = json.loads(query(runtime(save(future, expected=1))))
    second_context = capture()
    check('new physical terms append a new version and preserve the first',
          second['changed'] and second['facility']['version'] == 2 and
          json.loads(query(runtime(read(second_context)))) == first['facility'])
    reject(read(initial_context, 2), 'power_facility_unavailable')
    check('a known future-effective version is readable for future delivery pricing',
          json.loads(query(runtime(read(second_context, 2)))) == second['facility'])
    all_versions = json.loads(query(runtime(directory(second_context, 'all_known_versions_v1'))))
    active = json.loads(query(runtime(directory(second_context, 'active_asof_latest_v1'))))
    check('explicit directory policies distinguish future known versions from active as-of selection',
          [v['version'] for v in all_versions] == [1, 2] and [v['version'] for v in active] == [1])
    reject(directory(second_context, 'latest'), 'power_facility_selection_policy')
    check('directory selection never chooses an implicit newest-version policy')

    stale = json.loads(json.dumps(future))
    stale['terms']['thermal']['maximumOutputMw'] = '14'
    reject(save(stale, expected=1), 'power_facility_revision_conflict')
    older_fact = json.loads(query(runtime(save())))
    check('stale changed heads reject while a repeated older fact returns its original version',
          older_fact['facility'] == first['facility'] and not older_fact['changed'])
    conflicting_id = json.loads(json.dumps(stale))
    conflicting_id.update(id=str(uuid4()), version=3)
    reject(save(conflicting_id, expected=2), 'power_facility_revision_conflict')
    check('a physical location and kind cannot acquire a competing operating identity')

    historical_save = f"SELECT security.use_market_context('{catalog}','{second_context}');" + save()
    reject(historical_save, '42501')
    check('historical context authoring refuses even a business-fact replay')
    reject(f"SELECT security.use_market_context('{catalog}','{initial_context}');" + read(second_context), 'power_facility_context')
    check('an operation cannot replace its bound historical context')

    for label, mutate in [
        ('unknown physical field', lambda v: v['terms']['thermal'].update(guaranteedHeatRate='8')),
        ('missing physical input', lambda v: v['terms']['thermal'].pop('consumptionHeatRate')),
        ('inactive variant', lambda v: v['terms'].update(solar=dict(stage='generated'))),
        ('unknown envelope field', lambda v: v.update(name='Unretained duplicate master')),
    ]:
        bad = json.loads(json.dumps(facility))
        mutate(bad)
        reject(save(bad), 'power_facility_shape')
        check(label + ' cannot enter closed persisted setup')
    bad = json.loads(json.dumps(facility))
    bad['terms']['powerQuantityCode'] = 'test_power_quantity'
    reject(save(bad), 'power_facility_reference')
    bad['terms']['powerQuantityCode'] = 'test_power_megawatt'
    bad['terms']['clockCode'] = 'absent_clock'
    reject(save(bad), 'power_facility_reference')
    check('wrong units and incompatible exact clock references refuse')
    bad = json.loads(json.dumps(facility))
    bad['terms']['referenceRevisionId'] = str(uuid4())
    reject(save(bad), 'power_facility_reference')
    check('an absent exact relationship cannot substitute another reference version')
    for value in ('NaN', '0.0000000000000000001', '1000000000000000000000000'):
        bad = json.loads(json.dumps(facility))
        bad['terms']['thermal']['consumptionHeatRate'] = value
        reject(save(bad), 'power_facility_parameters')
    check('nonfinite and over-precision setup numbers reject without coercion')
    bad = json.loads(json.dumps(facility))
    bad['version'] = 3
    bad['terms']['thermal']['consumptionHeatRate'] = '0'
    reject(save(bad, expected=2), 'power_facility_parameters')
    check('the durable capability refuses a nonpositive incremental fuel conversion')

    reject('SELECT * FROM physical.power_operating_facility_versions;', '42501')
    reject('SELECT * FROM physical.power_operating_facility_requests;', '42501')
    reject(f"SELECT physical.power_operating_command({literal(facility)});", '42501')
    check('raw stored parameters, request records and private helpers stay inaccessible')
    status = f"SELECT physical.power_operating_facility_request_status('{catalog}','{request}');"
    check('request status returns the original durable outcome', json.loads(query(runtime(status))) == first)
    reject(f"SELECT security.use_market_context('{catalog}','{before}');" + status, 'power_facility_context')
    check('command recovery is explicit current control state and cannot leak later facts into historical reads')
    query("INSERT INTO security.role_permissions SELECT id,'ReadPhysical' FROM security.roles WHERE code='viewer';")
    check('request status is scoped to the current actor', query(runtime(status, 'viewer')) == '')
    reject(save(), '42501', actor='viewer')
    check('Physical read authority does not grant setup authoring')

    def reject_control(change, operation, code):
        try:
            query('BEGIN;' + change + runtime(operation).removeprefix('BEGIN;').removesuffix('COMMIT;') + 'ROLLBACK;')
        except RuntimeError as error:
            assert code in str(error), str(error)
        else:
            raise AssertionError('Revoked authority admitted operating access')

    revoke_read = "DELETE FROM security.role_permissions WHERE permission='ReadPhysical' AND role_id='a0000000-0000-0000-0000-000000000002';"
    reject_control(revoke_read, read(second_context), '42501')
    reject_control(revoke_read, status, '42501')
    revoke_write = "DELETE FROM security.role_permissions WHERE permission='ManagePhysicalSetup' AND role_id='a0000000-0000-0000-0000-000000000002';"
    reject_control(revoke_write, save(request=request), '42501')
    check('current revoked capabilities fence reads, status and request replay')
    revoke_reference = f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{reference}';"
    reject_control(revoke_reference, save(request=request), '42501')
    reject_control(revoke_reference, status, '42501')
    check('current reference scope fences both saved outcomes and retry replay')

    # A rolled-back command cannot leave a version, publication or retry receipt.
    rolled = json.loads(json.dumps(stale))
    rolled['version'] = 3
    rolled_request = str(uuid4())
    before_counts = query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';")
    query(runtime(save(rolled, rolled_request, 2)).removesuffix('COMMIT;') + 'ROLLBACK;')
    rolled_status = f"SELECT physical.power_operating_facility_request_status('{catalog}','{rolled_request}');"
    check('rollback leaves neither a publication nor an outcome receipt',
          query(runtime(rolled_status)) == '' and before_counts ==
          query(f"SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}';"))

    # The same facility's series cannot escape its exact physical identity.
    wrong_location = json.loads(json.dumps(rolled))
    wrong_location['terms']['facilityCode'] = 'test_node_a'
    reject(save(wrong_location, expected=0), 'power_facility_revision_conflict')
    check('an operating identity cannot be reused for a different physical location')

    solar = json.loads(json.dumps(facility))
    solar['id'] = str(uuid4())
    solar['terms'].update(kind='solar', thermal=None, solar=dict(stage='generated'), fuelQuantityCode=None, powerQuantityCode=None)
    solar_result = json.loads(query(runtime(save(solar))))
    check('solar consumes exact MWh and measurement stage without an unused MW relationship',
          solar_result['facility']['terms']['solar'] == solar['terms']['solar'] and solar_result['facility']['terms']['powerQuantityCode'] is None)
    unused_mw = json.loads(json.dumps(solar))
    unused_mw['terms']['powerQuantityCode'] = 'test_power_megawatt'
    reject(save(unused_mw), 'power_facility_shape')
    check('a solar supplied-production model cannot retain an unused power quantity')
    inert_solar = json.loads(json.dumps(solar))
    inert_solar['terms']['solar']['meterDeadlineMinutes'] = 60
    reject(save(inert_solar), 'power_facility_shape')
    check('a planned meter deadline without a current consumer cannot enter saved parameters')
    battery = json.loads(json.dumps(facility))
    battery['id'] = str(uuid4())
    battery['terms'].update(kind='battery', intervalMinutes=5, thermal=None, fuelQuantityCode=None, battery=dict(
        maximumChargeMw='0', maximumDischargeMw='3', minimumStoredMwh='0', maximumStoredMwh='12',
        chargeEfficiency='0.9', dischargeEfficiency='0.9', meterBoundary='grid_side_ac',
        lossPolicy='zero_auxiliary_and_self_discharge', capacityPolicy='fixed', simultaneousPolicy='prohibited',
        chargingOrigin='grid', throughputPolicy='uncapped', maximumThroughputMwh=None))
    battery_result = json.loads(query(runtime(save(battery))))
    check('battery supports a declared one-direction limit without a fabricated charge capability',
          battery_result['facility']['terms']['battery']['maximumChargeMw'] == '0')
    capped=json.loads(json.dumps(battery));capped['version']=2
    capped['terms']['battery'].update(throughputPolicy='grid_charge_plus_discharge',maximumThroughputMwh='18.100')
    capped_result=json.loads(query(runtime(save(capped,expected=1))))
    check('a consumed cumulative battery throughput cap saves its exact normalized value',
          capped_result['facility']['terms']['battery']['maximumThroughputMwh']=='18.1')

    if psql is not None:
        children = []
        gate_name = 'operating_gate_' + uuid4().hex

        def process(sql, keep_input=False):
            child = subprocess.Popen(psql, env=environment, text=True, stdin=subprocess.PIPE,
                                     stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            children.append(child)
            child.stdin.write(sql + '\n')
            child.stdin.flush()
            if not keep_input:
                child.stdin.close()
                child.stdin = None
            return child

        def wait_until(sql, limit=10):
            until = time.monotonic() + limit
            while time.monotonic() < until:
                if query(sql) == 't':
                    return
                time.sleep(0.02)
            raise AssertionError('Timed out waiting for the disposable test barrier')

        def collect(child, success=True, code=None):
            out, error = child.communicate(timeout=15)
            assert (child.returncode == 0) == success, error
            if code:
                assert code in error, error
            return out.strip()

        try:
            gate = process(f"SET application_name='{gate_name}';BEGIN;SELECT pg_advisory_xact_lock(730849102);", True)
            wait_until(f"SELECT EXISTS(SELECT 1 FROM pg_stat_activity a JOIN pg_locks l ON l.pid=a.pid WHERE a.application_name='{gate_name}' AND l.locktype='advisory' AND l.granted);")
            expired = process(runtime(save(rolled, expected=2), expiry=1))
            wait_until("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE wait_event='advisory' AND query LIKE '%save_power_operating_facility%');")
            time.sleep(1.1)
            gate.stdin.write('ROLLBACK;')
            gate.stdin.close()
            gate.stdin = None
            collect(gate)
            collect(expired, False, '42501')
            check('current authority is rechecked after a publication-lock wait expires the request')

            # Both callers pass the same expected head before the serialized save.
            duplicate_request = str(uuid4())
            concurrent = [process(runtime(save(rolled, duplicate_request, 2))) for _ in range(2)]
            outcomes = [json.loads(collect(child)) for child in concurrent]
            check('concurrent exact retries share one committed first outcome', outcomes[0] == outcomes[1] and outcomes[0]['facility']['version'] == 3)
            next_body = json.loads(json.dumps(rolled))
            next_body['version'] = 4
            next_body['terms']['thermal']['maximumOutputMw'] = '16'
            fact_retries = [process(runtime(save(next_body, expected=3))) for _ in range(2)]
            facts = [json.loads(collect(child)) for child in fact_retries]
            check('concurrent semantic retries with distinct request identities create one fact',
                  facts[0]['facility'] == facts[1]['facility'] and sorted(x['changed'] for x in facts) == [False, True])
            conflict_a = json.loads(json.dumps(next_body))
            conflict_a['version'] = 5
            conflict_a['terms']['thermal']['maximumOutputMw'] = '18'
            conflict_b = json.loads(json.dumps(conflict_a))
            conflict_b['terms']['thermal']['maximumOutputMw'] = '20'
            conflicts = [process(runtime(save(body, expected=4))) for body in (conflict_a, conflict_b)]
            results = [child.communicate(timeout=15) for child in conflicts]
            check('concurrent conflicting changes have one winner and one explicit stale-head refusal',
                  sorted(child.returncode == 0 for child in conflicts) == [False, True] and
                  any('power_facility_revision_conflict' in error for _, error in results))
        finally:
            for child in children:
                if child.poll() is None:
                    child.kill()
                    child.communicate(timeout=5)
    return passed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin', type=Path, required=True)
    args = parser.parse_args()
    binaries = {name: args.pg_bin.resolve() / name for name in ('initdb', 'pg_ctl', 'psql')}
    for binary in binaries.values():
        if not binary.is_file():
            parser.error('Missing preinstalled binary: ' + str(binary))
    environment = {key: value for key, value in os.environ.items() if not key.startswith('PG')}
    environment['PGTZ'] = 'UTC'
    temporary = Path(tempfile.mkdtemp(prefix='openetrm-operating-', dir='/tmp'))
    os.chmod(temporary, 0o700)
    data = temporary / 'data'
    startup_attempted = False
    safe_to_remove = True

    def run(command, **kwargs):
        return subprocess.run(command, env=environment, text=True, capture_output=True, timeout=60, **kwargs)

    try:
        result = run([str(binaries['initdb']), '-D', str(data), '--auth-local=trust', '--auth-host=reject',
                      '--encoding=UTF8', '--locale=C', '--no-instructions', '--username=operating_test'])
        if result.returncode:
            raise RuntimeError(result.stderr)
        options = shlex.join(['-k', str(temporary), '-h', '', '-p', '55439', '-c', 'track_commit_timestamp=on'])
        startup_attempted = True
        safe_to_remove = False
        result = run([str(binaries['pg_ctl']), '-D', str(data), '-w', '-t', '15', '-l',
                      str(temporary / 'server.log'), '-o', options, 'start'])
        if result.returncode:
            raise RuntimeError(result.stderr)
        psql = [str(binaries['psql']), '-X', '-h', str(temporary), '-p', '55439', '-U', 'operating_test',
                '-d', 'postgres', '-qAt', '-v', 'ON_ERROR_STOP=1', '-v', 'VERBOSITY=verbose']

        def query(sql):
            result = run(psql, input=sql)
            if result.returncode:
                raise RuntimeError(result.stderr)
            return result.stdout.strip()

        if Path(query('SHOW data_directory;')).resolve() != data.resolve():
            raise RuntimeError('Refusing any database outside the newly created cluster')
        query((ROOT / 'db/bootstrap.sql').read_text())
        for migration in sorted((ROOT / 'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql')):
            query(migration.read_text())
        from preview_fixture import fixture
        reference, market, security, manifest = fixture(ROOT)
        from native_read_transport import native_query
        query=native_query(query)
        query(reference)
        from seed_fixture_market import load_market
        security=load_market(query,market,security,manifest)
        if security.strip():query(security)
        print(f'{run_checks(query, manifest, psql, environment)} operating-facility checks passed', flush=True)
    finally:
        if startup_attempted:
            run([str(binaries['pg_ctl']), '-D', str(data), '-m', 'immediate', '-w', '-t', '15', 'stop'])
            status = run([str(binaries['pg_ctl']), '-D', str(data), 'status'])
            safe_to_remove = status.returncode == 3 and not (data / 'postmaster.pid').exists()
            if not safe_to_remove:
                print('Preserved owned cluster after uncertain shutdown: ' + str(temporary), flush=True)
        if safe_to_remove:
            shutil.rmtree(temporary)


if __name__ == '__main__':
    main()
