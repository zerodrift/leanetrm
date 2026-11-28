#!/usr/bin/env python3
"""Qualify an 18-to-19 copy upgrade using only a newly owned Docker container."""
import argparse
import difflib
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
import shlex
import subprocess
import sys
from uuid import uuid4

from preview_fixture import fixture, catalogue_fixture
from seed_fixture_market import load_fixture
from deploy_release import schema_fingerprint, SCHEMA_DUMP
from check_risk_jobs_db import provision_worker
from check_pilot_close import prepare as prepare_pilot_worker

ROOT = Path(__file__).resolve().parents[1]
OLD = 'postgres:18.7-bookworm@sha256:e742a2668255221388004f3a42b6150a2103f5054ab3a4a93ae9e4f01e551c35'
NEW = 'postgres:19.0-bookworm@sha256:f8b826440669ad283328d2ac8326682539eddd91867904ad5abf4ec19269d241'


def close_fixture(query, manifest):
    catalog, revision, reference, reference_revision, tenant, workspace = (
        manifest[key] for key in ('catalogId', 'revisionId', 'referenceCatalogId', 'referenceRevisionId', 'tenantId', 'workspaceId'))
    actor = query(f"SELECT principal_id FROM security.workspace_memberships WHERE(tenant_id,workspace_id,login_handle)=('{tenant}','{workspace}','approver');")
    query(f"""BEGIN;
INSERT INTO market_data.close_sessions VALUES('{catalog}','upgrade_utc','Synthetic upgrade close');
INSERT INTO market_data.close_session_versions(catalog_id,code,version,state,generation,definition_revision_id,
 reference_catalog_id,reference_revision_id,calendar_code,cutoff_local,effective_from,effective_to,actor_id,request_id,request,publisher_id,publish_request_id)
VALUES('{catalog}','upgrade_utc',1,'published',1,'{revision}','{reference}','{reference_revision}',
 'test_utc_every_day','16:00','2020-12-01','2021-12-01','{actor}','{uuid4()}','{{}}','{actor}','{uuid4()}');
INSERT INTO market_data.close_session_curves VALUES('{catalog}','upgrade_utc',1,'{revision}','future');
INSERT INTO presentation.close_session_listings VALUES('{catalog}','upgrade_utc',1,1,'Synthetic upgrade close',true,pg_current_xact_id());
COMMIT;""")

    def publish():
        identity = str(uuid4())
        query(f"""BEGIN;
INSERT INTO market_data.market_closes(catalog_id,id,definition_revision_id,reference_catalog_id,reference_revision_id,
 name,at,kind,actor_id,request_id,request,publication_order,publication_xid,session_code,session_version,business_date)
SELECT '{catalog}','{identity}','{revision}','{reference}','{reference_revision}',
 'Synthetic upgrade close','2021-01-15T16:00:00Z','retained_input','{actor}','{uuid4()}','{{}}',
 coalesce(max(publication_order),0)+1,pg_current_xact_id(),'upgrade_utc',1,'2021-01-15'
FROM market_data.market_closes WHERE catalog_id='{catalog}';
INSERT INTO market_data.market_close_members SELECT catalog_id,'{identity}',revision_id,curve_code,contract_id,ordinal
FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{catalog}','{revision}','future');
COMMIT;""")
        return identity

    return publish


def queued_risk_fixture(query, runtime, manifest):
    catalog, revision = manifest['catalogId'], manifest['revisionId']
    portfolio, book = str(uuid4()), str(uuid4())
    query(f"""BEGIN;
SELECT pg_advisory_xact_lock(730849102);
CREATE TEMP TABLE upgrade_book_publication ON COMMIT DROP AS
SELECT coalesce(max(publication_order),0)+1 AS ordinal FROM trading.publications WHERE catalog_id='{catalog}';
INSERT INTO trading.publications SELECT '{catalog}',ordinal,pg_current_xact_id() FROM upgrade_book_publication;
INSERT INTO trading.portfolios SELECT '{catalog}','{portfolio}','Synthetic upgrade portfolio',ordinal,'2020-01-01T00:00:00Z' FROM upgrade_book_publication;
INSERT INTO trading.books SELECT '{catalog}','{book}','{portfolio}','Synthetic upgrade book',ordinal,'2020-01-01T00:00:00Z' FROM upgrade_book_publication;
COMMIT;""")
    query(catalogue_fixture(manifest, 'https://preview.invalid'))
    selected = manifest['frames'][0]
    at = selected['at']
    books = json.loads(query(f"SELECT json_agg(id ORDER BY id) FROM trading.books WHERE catalog_id='{catalog}';"))
    curve = {'revision': {'catalogId': catalog, 'revisionId': revision}, 'code': next(iter(selected['curveContracts']))}
    factor = {'coordinate': 'market_quote', 'target': {'kind': 'parallel', 'curve': curve}}
    frame = {'id': str(uuid4()), 'name': 'Synthetic upgrade inputs', 'at': at, 'businessDate': at[:10],
             'observationIds': selected['observationIds'], 'curveContracts': selected['curveContracts'],
             'frozenObservationIds': [], 'sourceInputs': None, 'resolvesLatestCorrection': False,
             'source': {'definitionRevisionId': revision, 'referenceCatalogId': manifest['referenceCatalogId'],
                        'referenceRevisionId': manifest['referenceRevisionId']},
             'supplementalMarkets': {}, 'swapMarketSelections': {},
             'scenarioAxes': [{'id': 'a'*64, 'marketRevisionId': revision, 'factor': factor,
                               'points': [{'curve': curve, 'pointId': str(uuid4())}]}],
             'connectedMarkets': [{'revisionId': revision, 'manifestId': 'b'*64,
                                   'sourceExecutionConvention': 'contract_lot_execution_pnl_v2', 'recipes': [], 'samples': []}]}
    request = {'context': {'snapshotId': frame['id'], 'knowledgeMode': 'retained', 'convention': 'trading_workspace_v1'},
               'bookIds': books, 'portfolioIds': [],
               'bumps': [{'factor': factor, 'size': '0.01', 'convention': 'absolute_curve_coordinate_symmetric_v1'}],
               'convention': 'portfolio_instrument_repricing_v1', 'numericalPolicy': 'exact_rational_portfolio_repricing_v1',
               'detail': 'books', 'stress': [], 'engineVersion': 'synthetic_upgrade_engine_v1',
               'pricingPolicy': {'curveNumericalPolicy': 'exact_18_fractional_abs_lt_1e24_v1',
                                 'riskNumericalPolicy': 'exact_rational_portfolio_repricing_v1',
                                 'futuresConvention': 'contract_lot_execution_pnl_v2',
                                 'swapConvention': 'independent_side_financial_swap_v2',
                                 'rateNumericalPolicy': 'rate_curve_lattice30_to_even_18_v1',
                                 'discountInterpolationPolicy': 'log_discount_geometric_root_to_even_18_v1',
                                 'bumpConvention': 'absolute_quote_v1', 'bumpBasis': 'trading_tick', 'bumpTicks': 1}}
    literal = lambda value: "'"+json.dumps(value).replace("'", "''")+"'::jsonb"
    operation = str(uuid4())
    runtime(f"""DO $risk_fixture$ DECLARE context uuid;frame jsonb={literal(frame)};request jsonb={literal(request)};BEGIN
context=security.resolve_operation_context('{catalog}','{at[:10]}',NULL);
PERFORM security.use_market_context('{catalog}',context);
PERFORM security.use_trading_context('{catalog}','{at}');
frame=jsonb_set(frame,'{{contextId}}',to_jsonb(context));
request=jsonb_set(request,'{{context,contextId}}',to_jsonb(context));
PERFORM risk.submit('{catalog}','{operation}',context,'{revision}','{at}',request,frame);
END$risk_fixture$;""", profile='risk')
    return json.loads(query(f"SELECT row_to_json(j) FROM risk.jobs j WHERE request_id='{operation}';"))


def prepare_upgrade_workers(query, manifest):
    provision_worker(query, manifest, 'upgrade_risk_worker')
    prepare_pilot_worker(query, manifest)
    catalog = manifest['catalogId']
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']
    actor = query(f"SELECT principal_id FROM security.workspace_memberships WHERE(tenant_id,workspace_id,login_handle)=('{tenant}','{workspace}','approver');")
    query(f"""INSERT INTO security.close_session_grants(id,principal_id,tenant_id,workspace_id,catalog_id,session_code,granted_from,granted_to,revoked,actor_id,request_id)
SELECT gen_random_uuid(),principal_id,'{tenant}','{workspace}','{catalog}','upgrade_utc','2020-01-01','9999-01-01',false,'{actor}',gen_random_uuid()
FROM security.workspace_memberships WHERE(tenant_id,workspace_id)=('{tenant}','{workspace}') AND login_handle IN('approver','pilot_fixture_worker');""")


def occurrence_fixture(query, runtime, manifest, valuation_policy):
    catalog, revision = manifest['catalogId'], manifest['revisionId']
    mark = json.loads(query(f"""SELECT jsonb_build_object('seriesId',o.series_id,'anchorObservationId',o.id,'moveTicks',0,'value',o.value::text,'contractId',k.id)
FROM market_data.curve_members m JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(m.catalog_id,m.revision_id,m.contract_id)
JOIN market_data.curves c ON(c.catalog_id,c.revision_id,c.code)=(m.catalog_id,m.revision_id,m.curve_code)
JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.contract_id,s.quote_code)=(k.catalog_id,k.revision_id,k.id,c.quote_code)
JOIN market_data.observations o ON(o.catalog_id,o.definition_revision_id,o.series_id)=(s.catalog_id,s.revision_id,s.id)
WHERE(m.catalog_id,m.revision_id,m.curve_code)=('{catalog}','{revision}','future') AND k.delivery_from='2021-02-01' AND o.observed_at='2020-12-18T16:00:00Z';"""))
    members = [{'curve': 'future', 'contractId': mark.pop('contractId'), 'ordinal': 0}]
    pins = [{'definitionRevisionId': revision, 'seriesId': mark['seriesId'], 'observationId': mark['anchorObservationId'], 'inputUse': 'variable'}]
    literal = lambda value: "'"+json.dumps(value).replace("'", "''")+"'::jsonb"
    anchor = runtime(f"SELECT security.register_market_close('{catalog}','{revision}','{uuid4()}','Synthetic upgrade anchor','upgrade_utc','2020-12-18','2020-12-18T16:00:00Z',"
                     f"{literal(pins)},{literal([{**m, 'definitionRevisionId': revision, 'scope': 'root'} for m in members])});", profile='approver').splitlines()[-1]
    definition = {'session': 'upgrade_utc', 'priorCloseId': anchor,
                  'bookIds': [query(f"SELECT id FROM trading.books WHERE catalog_id='{catalog}' ORDER BY id LIMIT 1;")],
                  'curveCodes': ['future'], 'sourceCode': 'fixture', 'seed': 17,
                  'leafMoves': [{'curve': 'future', 'maxTicks': 7}], 'generator': 'seeded_clearing_tick_move_v1',
                  'tickBasis': 'clearing_increment', 'parameterPolicy': 'carry_exact_anchor_v1',
                  'frozenPolicy': 'carry_exact_anchor_frozen_facts_v1', 'membershipPolicy': 'retained_live_members_v1',
                  'calendarPolicy': 'every_utc_calendar_day_v1', 'closePolicy': 'utc_due_instant_v1', 'valuationPolicy': valuation_policy}
    now = datetime.now(timezone.utc)
    schedule = {'requestId': str(uuid4()), 'name': 'Synthetic upgrade occurrence', 'operation': 'generated_pilot_close_v1',
                'dueAt': (now+timedelta(minutes=10)).isoformat(), 'expiresAt': (now+timedelta(hours=1)).isoformat(),
                'intervalSeconds': None, 'timeZone': 'UTC', 'misfire': 'run_once', 'overlap': 'forbid',
                'timeoutSeconds': 3600, 'maximumAttempts': 3, 'profileId': None, 'profileVersion': None}
    job = json.loads(runtime(f'SELECT automation.create_pilot_close_job({literal(schedule)},{literal(definition)});', profile='approver').splitlines()[-1])
    query(f"UPDATE automation.jobs SET next_due_at='2020-12-19T16:00:00Z' WHERE id='{job['id']}';")

    def claim():
        return json.loads(query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler; SELECT automation.claim_next('{uuid4()}',120,true);"))

    def operation(claimed, sql):
        token = query(f"SET SESSION AUTHORIZATION pilot_fixture_scheduler; SELECT automation.issue_pilot_context('{claimed['id']}',{claimed['fence']},'{claimed['workerId']}');")
        return query(f"SET SESSION AUTHORIZATION pilot_fixture_runtime; BEGIN; SELECT security.use_context('{token}'); {sql} SELECT security.finish_operation_read(); COMMIT;")

    first = claim()
    operation(first, 'SELECT automation.admit_occurrence_inputs();')
    source = json.loads(operation(first, 'SELECT row_to_json(s) FROM automation.occurrence_read_inputs() s;'))
    return first, source, claim, operation, mark, members


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--ssh-host', help='Run Docker on this explicit development host')
    args = parser.parse_args()
    name = 'openetrm-upgrade-check-' + uuid4().hex
    image = name + ':test'
    created = False
    built = False

    def docker(*arguments, data=None):
        command = ['docker', *map(str, arguments)]
        if args.ssh_host:
            command = ['ssh', '-o', 'BatchMode=yes', args.ssh_host, shlex.join(command)]
        result = subprocess.run(command, input=data, capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError(result.stderr + result.stdout[-2000:])
        return result.stdout

    def execute(*arguments, data=None, user='postgres'):
        return docker('exec', '-i', '--user', user, '--workdir', '/tmp/qualification', name, *arguments, data=data)

    major = 18

    def query(sql):
        return execute(f'/usr/lib/postgresql/{major}/bin/psql', '-XqAt', '-h', '/tmp/qualification',
                       '-p', '55439', '-U', 'preview_owner', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', data=sql).strip()

    def schema_dump():
        return execute('/usr/lib/postgresql/19/bin/pg_dump', '-h', '/tmp/qualification',
                       '-p', '55439', '-U', 'preview_owner', '-d', 'postgres', '--schema-only').encode()

    def canonical_schema_dump(source_dump):
        # PG19 itself canonicalizes source18 parsed constraints/views. Compare
        # complete dumps afterward; never weaken schema checks by stripping SQL.
        directory = '/tmp/qualification/canonical'
        execute('/usr/lib/postgresql/19/bin/initdb', '-D', directory, '--auth-local=trust',
                '--auth-host=reject', '--encoding=UTF8', '--locale=C', '--username=preview_owner')
        execute('/usr/lib/postgresql/19/bin/pg_ctl', '-D', directory, '-l', directory+'.log',
                '-o', "-h '' -k /tmp/qualification -p 55440", '-w', 'start')
        try:
            roles = json.loads(query("SELECT json_agg(rolname ORDER BY rolname) FROM pg_roles WHERE left(rolname,3)<>'pg_' AND rolname<>'preview_owner';"))
            role_sql = ''.join('CREATE ROLE "'+role.replace('"','""')+'";' for role in roles)
            execute('/usr/lib/postgresql/19/bin/psql', '-XqAt', '-h', '/tmp/qualification', '-p', '55440',
                    '-U', 'preview_owner', '-d', 'postgres', '-v', 'ON_ERROR_STOP=1', data=role_sql+'\n'+source_dump.decode())
            return execute('/usr/lib/postgresql/19/bin/pg_dump', '-h', '/tmp/qualification', '-p', '55440',
                           '-U', 'preview_owner', '-d', 'postgres', '--schema-only').encode()
        finally:
            execute('/usr/lib/postgresql/19/bin/pg_ctl', '-D', directory, '-m', 'fast', '-w', 'stop')

    def start(version):
        execute(f'/usr/lib/postgresql/{version}/bin/pg_ctl', '-D', f'/tmp/qualification/pg{version}',
                '-l', f'/tmp/qualification/pg{version}.log', '-w', 'start')

    def stop(version):
        execute(f'/usr/lib/postgresql/{version}/bin/pg_ctl', '-D', f'/tmp/qualification/pg{version}', '-m', 'fast', '-w', 'stop')

    def check(label, condition=True):
        if not condition:
            raise AssertionError(label)
        print('PASS ' + label, flush=True)

    def rejected(sql, message):
        try:
            query(sql)
        except RuntimeError as error:
            if message in str(error):
                return
            raise
        raise AssertionError('Expected rejection: ' + message)

    try:
        docker('build', '--tag', image, '-', data=f'''FROM {OLD} AS old
FROM {NEW}
COPY --from=old /usr/lib/postgresql/18 /usr/lib/postgresql/18
COPY --from=old /usr/share/postgresql/18 /usr/share/postgresql/18
COPY --from=old /usr/share/doc/postgresql-18 /usr/share/doc/postgresql-18
COPY --from=old /usr/share/doc/postgresql-client-18 /usr/share/doc/postgresql-client-18
''')
        built = True
        docker('run', '-d', '--name', name, '--label', 'io.openetrm.qualification=postgresql-upgrade',
               '--network', 'none', '--memory', '4g', '--cpus', '4', '--entrypoint', 'sleep', image, 'infinity')
        created = True
        docker('exec', name, 'install', '-d', '-o', 'postgres', '-g', 'postgres', '/tmp/qualification')
        for version in (18, 19):
            execute(f'/usr/lib/postgresql/{version}/bin/initdb', '-D', f'/tmp/qualification/pg{version}',
                    '--auth-local=trust', '--auth-host=reject', '--encoding=UTF8', '--locale=C', '--username=preview_owner')
            execute('sh', '-c', f'cat >> /tmp/qualification/pg{version}/postgresql.conf',
                    data="listen_addresses=''\nunix_socket_directories='/tmp/qualification'\nport=55439\ntrack_commit_timestamp=on\ntimezone='UTC'\n")
        start(18)
        query((ROOT / 'db/bootstrap.sql').read_text())
        journal = sorted((ROOT / 'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql'))
        migration = next(path for path in journal if path.name.startswith('0414_'))
        query('\n'.join(path.read_text() for path in journal if path < migration))
        for alteration in (
            'GRANT EXECUTE ON FUNCTION security.checked_commit_time(xid8) TO PUBLIC;',
            'ALTER FUNCTION security.checked_commit_time(xid8) STABLE;',
            'ALTER FUNCTION security.coordinates_from_inputs(uuid,date,timestamptz,text,integer) SET search_path=public;',
            'GRANT EXECUTE ON FUNCTION power_data.certify(uuid) TO openetrm_runtime;'):
            rejected('BEGIN; '+alteration+'\n'+migration.read_text(), 'postgresql_upgrade_helper')
        check('migration refuses privilege, volatility and configuration drift')
        query('\n'.join(path.read_text() for path in journal if path >= migration))
        check('the complete migration journal applies on the source major')
        reference, market, security, manifest = fixture(ROOT)
        load_fixture(query, reference, market, security, manifest)
        catalog = manifest['catalogId']
        publish_close = close_fixture(query, manifest)
        old_close = publish_close()

        def runtime(sql, profile='viewer'):
            token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                          f"'https://preview.invalid','{profile}','human_interactive','{manifest['tenantId']}',"
                          f"'{manifest['workspaceId']}',NULL,'preview_runtime',120);")
            return query(f"SET SESSION AUTHORIZATION preview_runtime; BEGIN; SELECT security.use_context('{token}'); {sql} SELECT security.finish_operation_read(); COMMIT;")

        prepare_upgrade_workers(query, manifest)
        risk_job = queued_risk_fixture(query, runtime, manifest)
        check('a real authorized risk request is queued before upgrade', risk_job['state'] == 'queued')
        occurrence, occurrence_source, claim_occurrence, pilot, pilot_mark, pilot_members = occurrence_fixture(
            query, runtime, manifest, risk_job['request']['pricingPolicy'])
        check('a real scheduled occurrence admits its inputs before upgrade', occurrence_source['effective_on'] == '2020-12-19')
        history_sql = f"SELECT security.close_history('{catalog}','upgrade_utc','2021-01-15');"
        old_history = json.loads(runtime(f"SELECT security.resolve_operation_context('{catalog}','2021-02-15',NULL); "+history_sql).splitlines()[-1])
        check('source close history returns its real retained close', old_history['inForce']['id'] == old_close)
        query(f"INSERT INTO presentation.close_session_listings VALUES('{catalog}','upgrade_utc',2,1,'Synthetic revised listing',true,pg_current_xact_id());")
        listing_commit = query(f"SELECT security.checked_commit_time(publication_xid) FROM presentation.close_session_listings WHERE catalog_id='{catalog}' AND code='upgrade_utc' AND version=2;")
        check('the new listing remains uncertified before upgrade', query(f"SELECT count(*) FROM presentation.close_session_listing_receipts WHERE catalog_id='{catalog}' AND code='upgrade_utc' AND version=2;") == '0')
        # Exercise a used identity sequence, including its semantic allocator state.
        query("SELECT nextval('automation.audit_sequence_seq');")
        source = json.loads(runtime(f"SELECT security.resolve_operation_context('{catalog}','2021-02-15',NULL); "
                                    "SELECT row_to_json(s) FROM security.operation_read_inputs s;").splitlines()[-1])
        before = query(f"SELECT json_agg(k ORDER BY publication_order) FROM market_data.knowledge_publications k WHERE catalog_id='{catalog}';")
        origin = query(f"SELECT origin_xid FROM market_data.knowledge_clocks WHERE catalog_id='{catalog}';")
        committed = query(f"SELECT security.checked_commit_time('{origin}'::xid8);")
        old_system = query('SELECT (pg_control_system()).system_identifier;')
        # This is the first behavior gate: an unmodified schema has no way to
        # preserve its exact commit evidence before the native metadata is lost.
        source_dump = schema_dump()
        native_schema = schema_fingerprint(source_dump)
        expected_dump = canonical_schema_dump(source_dump)
        source_schema = schema_fingerprint(expected_dump)
        request_id = str(uuid4())
        prepare = f"SELECT row_to_json(s) FROM security.prepare_postgresql_upgrade('{request_id}','{native_schema}','{source_schema}') s;"
        for isolation in ('REPEATABLE READ', 'SERIALIZABLE'):
            rejected(f'BEGIN ISOLATION LEVEL {isolation}; '+prepare, 'postgresql_upgrade_snapshot')
        rejected('BEGIN; CREATE TABLE public.unreviewed(publication_xid xid8); '+prepare, 'postgresql_upgrade_inventory')
        rejected('BEGIN; CREATE UNLOGGED TABLE public.unreviewed(id integer); '+prepare, 'postgresql_upgrade_relation')
        rejected('SET SESSION AUTHORIZATION preview_runtime; '+prepare, 'permission denied')
        check('preparation refuses unreviewed storage, stale snapshots and runtime authority')
        seal = json.loads(query(prepare))
        check('retrying source preparation returns its original receipt', json.loads(query(prepare)) == seal)
        for table in ('source', 'times'):
            rejected(f"DELETE FROM security.postgresql_upgrade_{table};", 'Knowledge receipts are immutable')
        check('the sealed source records its actual physical identity', seal['source_system_id'] == old_system)
        abandoned = seal
        query("INSERT INTO market_data.catalogs VALUES('40000000-0000-0000-0000-000000000097','resumed_upgrade');")
        check('a retried preparation keeps its first outcome after old-major writes', json.loads(query(prepare)) == abandoned)
        request_id = str(uuid4())
        reseal = f"SELECT row_to_json(s) FROM security.prepare_postgresql_upgrade('{request_id}','{native_schema}','{source_schema}') s;"
        rejected(f"BEGIN; ALTER TABLE security.postgresql_upgrade_times DISABLE TRIGGER immutable_upgrade_proof; DELETE FROM security.postgresql_upgrade_times WHERE source_id='{abandoned['id']}' AND full_xid='{origin}'; "+reseal, 'postgresql_upgrade_proof')
        rejected(f"BEGIN; INSERT INTO security.postgresql_upgrade_times VALUES('{abandoned['id']}','{abandoned['seal_xid']}',clock_timestamp()); "+reseal, 'postgresql_upgrade_proof')
        check('a new attempt refuses incomplete or extended earlier timestamp evidence')
        seal = json.loads(query(reseal))
        check('an explicit later preparation preserves the abandoned proof', seal['data_digest'] != abandoned['data_digest'] and query('SELECT count(*) FROM security.postgresql_upgrade_source;') == '2')
        stop(18)
        execute('/usr/lib/postgresql/19/bin/pg_upgrade', '--old-bindir=/usr/lib/postgresql/18/bin',
                '--new-bindir=/usr/lib/postgresql/19/bin', '--old-datadir=/tmp/qualification/pg18',
                '--new-datadir=/tmp/qualification/pg19', '--username=preview_owner', '--socketdir=/tmp/qualification',
                '--old-port=55439', '--new-port=55440')
        major = 19
        start(19)
        check('a real copy upgrade changes the physical database identity', query('SELECT (pg_control_system()).system_identifier;') != old_system)
        rejected(f"SELECT security.checked_commit_time('{origin}'::xid8);", 'knowledge_retention')
        wrong_digest = ('0' if seal['data_digest'][0] != '0' else '1') + seal['data_digest'][1:]
        target_dump = schema_dump()
        target_schema = schema_fingerprint(target_dump)
        if target_schema != source_schema:
            difference = difflib.unified_diff(SCHEMA_DUMP.match(expected_dump)['schema'].decode().splitlines(),
                                             SCHEMA_DUMP.match(target_dump)['schema'].decode().splitlines())
            raise AssertionError('Upgraded schema differs:\n' + '\n'.join(list(difference)[:100]))
        check('the complete schema, owners and ACLs survive unchanged', target_schema == source_schema)
        rejected(f"SELECT security.admit_postgresql_upgrade('{abandoned['id']}','{abandoned['data_digest']}','{target_schema}');", 'postgresql_upgrade_proof')
        rejected(f"SELECT security.admit_postgresql_upgrade('{request_id}','{wrong_digest}','{target_schema}');", 'postgresql_upgrade_proof')
        admit = f"SELECT row_to_json(a) FROM security.admit_postgresql_upgrade('{request_id}','{seal['data_digest']}','{target_schema}') a;"
        rejected('SET SESSION AUTHORIZATION preview_runtime; '+admit, 'permission denied')
        for isolation in ('REPEATABLE READ', 'SERIALIZABLE'):
            rejected(f'BEGIN ISOLATION LEVEL {isolation}; '+admit, 'postgresql_upgrade_snapshot')
        for authority_change in (
            'ALTER ROLE preview_runtime NOLOGIN;',
            'GRANT pg_read_all_data TO preview_runtime;',
            'ALTER DATABASE postgres CONNECTION LIMIT 99;'):
            rejected('BEGIN; '+authority_change+admit, 'postgresql_upgrade_proof')
        check('changed login authority, built-in privilege membership and database permissions refuse admission')
        rejected(f"BEGIN; ALTER TABLE security.postgresql_upgrade_times DISABLE TRIGGER immutable_upgrade_proof; DELETE FROM security.postgresql_upgrade_times WHERE source_id='{request_id}' AND full_xid='{origin}'; "+admit, 'postgresql_upgrade_proof')
        sequence = query("SELECT c.oid::regclass FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE c.relkind='S' AND n.nspname='automation' ORDER BY c.relname LIMIT 1;")
        sequence_state = json.loads(query(f'SELECT jsonb_build_array(last_value,is_called) FROM {sequence};'))
        try:
            query(f"SELECT setval('{sequence}',{sequence_state[0]+1},true);")
            rejected(admit, 'postgresql_upgrade_proof')
        finally:
            query(f"SELECT setval('{sequence}',{sequence_state[0]},{str(sequence_state[1]).lower()});")
        check('missing commit evidence and changed sequence allocators refuse admission')
        # A copied schema with changed business rows must not acquire continuity.
        rejected("BEGIN; INSERT INTO market_data.catalogs VALUES('40000000-0000-0000-0000-000000000099','changed_upgrade'); "+admit, 'postgresql_upgrade_proof')
        admitted = json.loads(query(admit))
        check('admission retries return their first durable outcome', json.loads(query(admit)) == admitted)
        check('the upgrade retains the exact engine commit time', query(f"SELECT security.checked_commit_time('{origin}'::xid8);") == committed)
        coordinates = (f"SELECT row_to_json(s) FROM security.coordinates_from_inputs('{catalog}','2021-02-15',"
                       f"'{source['known_at']}','{source['system_id']}',{source['timeline']}) s;")
        replay = json.loads(query(coordinates))
        check('every original source coordinate survives the physical upgrade', replay == source)
        check('immutable publication evidence is unchanged', before == query(f"SELECT json_agg(k ORDER BY publication_order) FROM market_data.knowledge_publications k WHERE catalog_id='{catalog}';"))
        retained_read = (f"SELECT security.resolve_operation_context('{catalog}','2021-02-15','{source['known_at']}'); "
                         f"SELECT security.use_market_context('{catalog}','{source['id']}'); ")
        check('close history preserves its exact recordedAt after native timestamps disappear', json.loads(runtime(retained_read+history_sql).splitlines()[-1]) == old_history)
        query(f"SELECT security.current_close_session_listing('{catalog}','upgrade_utc');")
        check('a never-read listing certifies the original engine time after upgrade', query(f"SELECT known_from FROM presentation.close_session_listing_receipts WHERE catalog_id='{catalog}' AND code='upgrade_utc' AND version=2;") == listing_commit)
        runtime(retained_read+f"SELECT security.register_read_source('{catalog}','{source['id']}','2021-02-15','{source['known_at']}',{source['schedule_prefix']},'{source['system_id']}',{source['timeline']});")
        check('fresh and retained readers of the same coordinates share one source identity')
        new_close = publish_close()
        check('a later committed close cannot enter the old knowledge view', json.loads(runtime(retained_read+history_sql).splitlines()[-1]) == old_history)
        current_history = json.loads(runtime(f"SELECT security.resolve_operation_context('{catalog}','2021-02-15',NULL); "+history_sql).splitlines()[-1])
        check('fresh reads select the new close through native post-upgrade commit metadata', current_history['inForce']['id'] == new_close)
        runtime(f"SELECT security.resolve_operation_context('{catalog}','2021-02-15',NULL);")
        check('new authorized reads certify on the admitted physical successor')
        check('the retained queued risk owner survives unchanged', json.loads(query(f"SELECT row_to_json(j) FROM risk.jobs j WHERE id='{risk_job['id']}';")) == risk_job)
        claim = json.loads(query("SET SESSION AUTHORIZATION upgrade_risk_worker; SELECT risk.claim(30,'synthetic_upgrade_engine_v1');"))
        query(f"SET SESSION AUTHORIZATION upgrade_risk_worker; BEGIN; SELECT risk.bind('{claim['jobId']}','{claim['leaseToken']}'); SELECT security.finish_operation_read(); COMMIT;")
        check('a newly claimed worker binds the pre-upgrade retained source under current authority', claim['jobId'] == risk_job['id'])
        query(f"UPDATE automation.runs SET lease_until=clock_timestamp()-interval '1 second' WHERE id='{occurrence['id']}';")
        recovered = claim_occurrence()
        recovered_source = json.loads(pilot(recovered, 'SELECT row_to_json(s) FROM automation.occurrence_read_inputs() s;'))
        source_fields = tuple(field for field in occurrence_source if field != 'certified_through')
        check('scheduler recovery reuses the exact original owner coordinates under a new fence',
              recovered['id'] == occurrence['id'] and recovered['fence'] > occurrence['fence'] and
              all(recovered_source[field] == occurrence_source[field] for field in source_fields))
        literal = lambda value: "'"+json.dumps(value).replace("'", "''")+"'::jsonb"
        pilot(recovered, f"SELECT security.use_market_context('{catalog}',(automation.occurrence_read_inputs()).id); "
              f"SELECT security.use_trading_context('{catalog}','2020-12-19T16:00:00Z'); "
              f"SELECT automation.publish_pilot_close({literal([pilot_mark])},'[]'::jsonb,{literal(pilot_members)});")
        new_commit = pilot(recovered, 'SELECT automation.pilot_close_commit();')
        runtime(f"SELECT security.resolve_operation_context('{catalog}','2020-12-19',NULL);")
        derived = json.loads(query(f"SELECT row_to_json(s) FROM security.coordinates_from_inputs('{catalog}','2020-12-19','{new_commit}',"
                                   f"'{occurrence_source['system_id']}',{occurrence_source['timeline']}) s;"))
        check('a pre-upgrade occurrence can publish its close and resolve the exact new commit on its retained lineage',
              derived['system_id'] == occurrence_source['system_id'] and
              datetime.fromisoformat(derived['known_at']) == datetime.fromisoformat(new_commit))
        rejected(f"SELECT security.checked_commit_time(({origin}::numeric+4294967296)::text::xid8);", 'knowledge_retention')
        rejected(f"SELECT security.coordinates_from_inputs('{catalog}','2021-02-15','{source['known_at']}','{source['system_id']}',{source['timeline']+1});", 'knowledge_coverage')
        query("INSERT INTO market_data.catalogs VALUES('40000000-0000-0000-0000-000000000098','new_upgrade');")
        check('new catalogs use the successor physical source', query("SELECT system_identifier=(pg_control_system()).system_identifier::text FROM market_data.knowledge_clocks WHERE catalog_id='40000000-0000-0000-0000-000000000098';") == 't')
        check('admission remains idempotent after new business writes', json.loads(query(admit)) == admitted)
        print('PostgreSQL 18-to-19 qualification passed.', flush=True)
    finally:
        original_error = sys.exception()
        cleanup_errors = []
        if created:
            try:
                docker('rm', '-f', '-v', name)
            except RuntimeError as error:
                cleanup_errors.append(str(error))
        if built:
            try:
                docker('image', 'rm', image)
            except RuntimeError as error:
                cleanup_errors.append(str(error))
        if cleanup_errors:
            message = f'Cleanup of owned qualification {name} failed: ' + '\n'.join(cleanup_errors)
            if original_error is None:
                raise RuntimeError(message)
            print(message, file=sys.stderr)


if __name__ == '__main__':
    main()
