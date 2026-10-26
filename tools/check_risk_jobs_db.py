#!/usr/bin/env python3
"""Verify durable risk ownership only in a newly allocated stock PostgreSQL cluster."""
import argparse
from copy import deepcopy
import json
import os
from pathlib import Path
import select
import shlex
import shutil
import subprocess
import tempfile
import threading
import time
from uuid import uuid4

from preview_fixture import fixture, trading_fixture

ROOT = Path(__file__).resolve().parents[1]


def provision_worker(query, manifest, login='preview_risk_worker'):
    if not login.replace('_', '').isalnum():
        raise ValueError('Worker fixture login must be a simple identifier')
    catalog, tenant, workspace = (manifest[key] for key in ('catalogId', 'tenantId', 'workspaceId'))
    service = str(uuid4())
    query(f"""CREATE ROLE {login} LOGIN IN ROLE openetrm_risk_worker;
    INSERT INTO security.principals VALUES('{service}','service',true);
    INSERT INTO security.workspace_memberships VALUES('{tenant}','{workspace}','{service}','{login}','Synthetic risk worker',true,1);
    INSERT INTO security.role_grants SELECT gen_random_uuid(),'{service}',id,'{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false FROM security.roles WHERE code='risk_analyst';
    INSERT INTO risk.workers VALUES('{login}','{service}','{tenant}','{workspace}','{catalog}',true);""")


def run_checks(query, manifest, psql=None, environment=None):
    assert query("SELECT to_regclass('risk.jobs') IS NOT NULL;") == 't', 'Durable risk storage is missing'
    catalog, revision, tenant, workspace = (manifest[key] for key in ('catalogId', 'revisionId', 'tenantId', 'workspaceId'))
    provision_worker(query, manifest, 'synthetic_risk_worker')
    provision_worker(query, manifest, 'synthetic_other_worker')
    frame_seed = manifest['frames'][0]
    at = frame_seed['at']
    frame = {'id': str(uuid4()), 'name': 'Synthetic retained inputs', 'at': at,
             'observationIds': frame_seed['observationIds'], 'curveContracts': frame_seed['curveContracts'],
             'frozenObservationIds': []}

    def token(profile='risk'):
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','{profile}','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")

    def user(sql, profile='risk'):
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token(profile)}');{sql};COMMIT;")

    context = user(f"SELECT security.capture_date_context('{catalog}','{at[:10]}',NULL)")
    frame['contextId'] = context
    books = json.loads(user(f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');SELECT coalesce(jsonb_agg(id),'[]'::jsonb) FROM trading.books WHERE catalog_id='{catalog}'"))
    assert books, 'Fixture must contain an explicit book scope'
    curve = {'revision': {'catalogId': catalog, 'revisionId': revision}, 'code': next(iter(frame['curveContracts']))}
    factor = {'coordinate': 'market_quote', 'target': {'kind': 'parallel', 'curve': curve}}
    axis = {'id': 'a' * 64, 'marketRevisionId': revision, 'factor': factor,
            'points': [{'curve': curve, 'pointId': str(uuid4())}]}
    frame.update(supplementalMarkets={}, swapMarketSelections={}, scenarioAxes=[axis],
                 connectedMarkets=[{'revisionId': revision, 'manifestId': 'b' * 64,
                     'sourceExecutionConvention': 'fixed_total_cash_execution_pnl_v1', 'recipes': [], 'samples': []}])
    request = {'context': {'snapshotId': frame['id'], 'contextId': context, 'knowledgeMode': 'retained', 'convention': 'trading_workspace_v1'},
               'bookIds': books, 'portfolioIds': [],
               'bumps': [{'factor': factor, 'size': '0.01', 'convention': 'absolute_curve_coordinate_symmetric_v1'}],
               'convention': 'portfolio_instrument_repricing_v1', 'numericalPolicy': 'exact_rational_portfolio_repricing_v1',
               'detail': 'books', 'stress': [], 'engineVersion': 'synthetic_engine_v1',
               'pricingPolicy': {'curveNumericalPolicy': 'exact_18_fractional_abs_lt_1e24_v1',
                                 'riskNumericalPolicy': 'exact_rational_portfolio_repricing_v1',
                                 'futuresConvention': 'fixed_total_cash_execution_pnl_v1',
                                 'swapConvention': 'independent_side_financial_swap_v2',
                                 'rateNumericalPolicy': 'simple_actual365fixed_rational_to_even_18_v1',
                                 'discountInterpolationPolicy': 'log_discount_geometric_root_to_even_18_v1',
                                 'bumpConvention': 'absolute_quote_v1', 'bumpBasis': 'trading_tick', 'bumpTicks': 1}}
    def literal(value):
        return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"

    def submit_sql(operation=None, payload=None, selected_frame=None, selected_context=None):
        retained = selected_context or context
        return f"SELECT security.use_market_context('{catalog}','{retained}');SELECT security.use_trading_context('{catalog}','{at}');SELECT risk.submit('{catalog}','{operation or uuid4()}','{retained}','{revision}','{at}',{literal(payload or request)},{literal(selected_frame or frame)})"

    def submit(operation=None, payload=None, selected_frame=None, selected_context=None):
        return json.loads(user(submit_sql(operation, payload, selected_frame, selected_context)))

    def worker(sql, login='synthetic_risk_worker'):
        return query(f"SET SESSION AUTHORIZATION {login};{sql};")

    def reject(sql, expected):
        try:
            sql()
            raise AssertionError(f'Accepted operation expected to reject with {expected}')
        except RuntimeError as error:
            assert expected in str(error), str(error)

    # An isolated configuration drift must be refused before any work is claimed.
    # Every injected privilege is rolled back, including when the expected denial
    # aborts its transaction; no fixture or later test retains elevated authority.
    role = 'synthetic_risk_worker'
    changes = [f'GRANT {membership} TO {role}' for membership in
               ('openetrm_collector', 'openetrm_reference_importer', 'openetrm_scheduler', 'openetrm_security_owner')]
    changes += [f'ALTER ROLE {role} {privilege}' for privilege in
                ('SUPERUSER', 'BYPASSRLS', 'CREATEDB', 'CREATEROLE', 'NOLOGIN')]
    changes += [f'CREATE TABLE public.synthetic_risk_owned(value integer);ALTER TABLE public.synthetic_risk_owned OWNER TO {role}',
                f'CREATE SCHEMA synthetic_risk_owned AUTHORIZATION {role}',
                f'ALTER DATABASE postgres OWNER TO {role}']
    for change in changes:
        reject(lambda: query(f"BEGIN;{change};SET SESSION AUTHORIZATION {role};SELECT risk.claim(30,'unmatched_engine');ROLLBACK;"),
               'risk_job_denied')
    print('PASS risk worker boundary: mixed executor roles, privileged flags and application ownership refuse admission', flush=True)

    supplemental = next(m for m in manifest['swapMarkets'] if m['revisionId'] != revision)
    supplemental_revision = supplemental['revisionId']
    supplemental_seed = next(f for f in supplemental['frames'] if f['at'] == at)
    member = json.loads(query(f"SELECT jsonb_build_object('curve',curve_code,'contract',contract_id) FROM market_data.curve_members WHERE catalog_id='{catalog}' AND revision_id='{supplemental_revision}' ORDER BY curve_code,ordinal LIMIT 1;"))
    supplemental_frame = {key: value for key, value in frame.items()
                          if key not in ('supplementalMarkets', 'swapMarketSelections', 'scenarioAxes', 'connectedMarkets')}
    supplemental_frame.update(name='Synthetic supplemental market', observationIds=supplemental_seed['observationIds'],
                              frozenObservationIds=supplemental_seed['observationIds'][:1],
                              curveContracts={member['curve']: [member['contract']]})
    generic_frame = {**frame, 'supplementalMarkets': {supplemental_revision: supplemental_frame},
                     'swapMarketSelections': {supplemental_revision: supplemental_frame['observationIds']},
                     'connectedMarkets': [*frame['connectedMarkets'],
                         {'revisionId': supplemental_revision, 'manifestId': 'c' * 64,
                          'sourceExecutionConvention': 'fixed_total_cash_execution_pnl_v1', 'recipes': [], 'samples': []}]}
    generic_request = {**request, 'engineVersion': 'synthetic_portfolio_engine_v1'}
    # The cutover refuses old financial operations and missing graph/axis pins;
    # each rejection is exercised against the same otherwise valid fixture.
    reject(lambda: submit(payload={**request, 'convention': 'synthetic_legacy_futures_v1'}), 'risk_job_input')
    reject(lambda: submit(selected_frame={key: value for key, value in frame.items() if key != 'connectedMarkets'}), 'risk_graph_input')
    reject(lambda: submit(selected_frame={**frame, 'connectedMarkets': []}), 'risk_graph_input')
    reject(lambda: submit(selected_frame={key: value for key, value in frame.items() if key != 'scenarioAxes'}), 'risk_axis_input')
    reject(lambda: submit(selected_frame={**frame, 'scenarioAxes': []}), 'risk_axis_input')
    reject(lambda: submit(payload={**request, 'bumps': [{'seriesId': str(uuid4()), 'quote': 'synthetic_old_quote', 'size': '0.01', 'convention': 'absolute_quote_v1'}]}), 'risk_axis_input')
    generic_operation = str(uuid4())
    generic_job = submit(generic_operation, generic_request, generic_frame)
    assert generic_job.get('convention') == generic_request['convention'], 'Job history omits its report convention'
    assert submit(generic_operation, generic_request, generic_frame)['jobId'] == generic_job['jobId']
    assert query(f"SELECT count(*) FROM risk.job_markets WHERE job_id='{generic_job['jobId']}' AND revision_id='{supplemental_revision}';") == '1'
    assert query(f"SELECT count(*) FROM risk.job_market_observations WHERE job_id='{generic_job['jobId']}';") == str(len(supplemental_frame['observationIds']))
    assert query(f"SELECT count(*) FROM risk.job_market_contracts WHERE job_id='{generic_job['jobId']}';") == '1'
    assert query(f"SELECT count(*) FROM risk.job_market_observations WHERE job_id='{generic_job['jobId']}' AND frozen;") == '1'
    assert query(f"SELECT count(*) FROM risk.job_scenario_axes WHERE job_id='{generic_job['jobId']}';") == '1'
    assert query(f"SELECT count(*) FROM risk.job_scenario_points WHERE job_id='{generic_job['jobId']}';") == '1'
    for mutate in (
        lambda value: value['connectedMarkets'].pop(),
        lambda value: value['scenarioAxes'][0].update(marketRevisionId=str(uuid4())),
        lambda value: value['scenarioAxes'][0].update(points=[]),
        lambda value: value['scenarioAxes'][0]['points'][0]['curve'].update(code='different_runtime_curve'),
        lambda value: value['scenarioAxes'][0]['factor'].update(coordinate='inferred'),
        lambda value: value['scenarioAxes'][0]['factor']['target'].update(kind='delivery_bucket', **{'from': '2020-12-15', 'to': '2020-12-14', 'convention': 'whole_point_delivery_contained_half_open_v1'}),
    ):
        invalid = deepcopy(generic_frame); mutate(invalid)
        expected = 'risk_graph_input' if len(invalid['connectedMarkets']) < len(generic_frame['connectedMarkets']) else 'risk_axis_input'
        reject(lambda: submit(payload=generic_request, selected_frame=invalid), expected)
    for malformed in (
        {**request['bumps'][0], 'size': '0'}, {**request['bumps'][0], 'size': 'NaN'},
        {**request['bumps'][0], 'size': '0.0000000000000000001'},
        {**request['bumps'][0], 'convention': 'absolute_quote_v1'},
    ):
        reject(lambda: submit(payload={**request, 'bumps': [malformed]}), 'risk_axis_input')
    # A compiled curve has an explicit synthetic identity; its authority is the
    # exact retained graph, not an invented foreign key to market definitions.
    computed_frame = deepcopy(frame)
    computed = computed_frame['scenarioAxes'][0]
    computed['factor']['coordinate'] = 'computed_output'
    computed['factor']['target']['curve']['revision']['revisionId'] = str(uuid4())
    computed['points'][0]['curve'] = deepcopy(computed['factor']['target']['curve'])
    computed_request = deepcopy(request)
    computed_request['bumps'][0]['factor'] = deepcopy(computed['factor'])
    computed_job = submit(payload=computed_request, selected_frame=computed_frame)
    user(f"SELECT risk.cancel('{computed_job['jobId']}')")
    supplemental_only = deepcopy(generic_frame)
    supplemental_only['connectedMarkets'] = supplemental_only['connectedMarkets'][1:]
    selected_axis = supplemental_only['scenarioAxes'][0]
    selected_axis['marketRevisionId'] = supplemental_revision
    selected_axis['factor']['target']['curve'] = {'revision': {'catalogId': catalog, 'revisionId': supplemental_revision}, 'code': member['curve']}
    selected_axis['points'][0]['curve'] = deepcopy(selected_axis['factor']['target']['curve'])
    supplemental_request = deepcopy(generic_request)
    supplemental_request['bumps'][0]['factor'] = deepcopy(selected_axis['factor'])
    supplemental_job = submit(payload=supplemental_request, selected_frame=supplemental_only)
    user(f"SELECT risk.cancel('{supplemental_job['jobId']}')")
    for table in ('job_scenario_axes', 'job_scenario_points'):
        reject(lambda: query(f"UPDATE risk.{table} SET job_id=job_id WHERE job_id='{generic_job['jobId']}';"), 'immutable')
        assert query(f"SELECT NOT has_table_privilege('openetrm_runtime','risk.{table}','SELECT') AND NOT has_table_privilege('openetrm_risk_worker','risk.{table}','INSERT');") == 't'
    print('PASS connected risk axes: mandatory graph coverage, typed coordinates, resolved point custody, numeric bounds and immutable private pins', flush=True)
    empty_markets = submit(payload=generic_request, selected_frame={**frame, 'supplementalMarkets': {}, 'swapMarketSelections': {}})
    user(f"SELECT risk.cancel('{empty_markets['jobId']}')")
    reject(lambda: submit(payload={key: value for key, value in generic_request.items() if key != 'pricingPolicy'}, selected_frame=generic_frame), 'risk_job_input')
    for mutate in (
        lambda value: value.pop('supplementalMarkets'),
        lambda value: value['supplementalMarkets'][supplemental_revision].update(contextId=str(uuid4())),
        lambda value: value['supplementalMarkets'][supplemental_revision].update(id=str(uuid4())),
        lambda value: value['supplementalMarkets'][supplemental_revision].update(at='2020-12-14T16:00:00Z'),
        lambda value: value['supplementalMarkets'][supplemental_revision].update(frozenObservationIds=[str(uuid4())]),
        lambda value: value['supplementalMarkets'][supplemental_revision].update(supplementalMarkets={}),
        lambda value: value.update(swapMarketSelections={}),
    ):
        invalid = deepcopy(generic_frame); mutate(invalid)
        reject(lambda: submit(payload=generic_request, selected_frame=invalid), 'risk_job_input')
    invalid = deepcopy(generic_frame)
    invalid['supplementalMarkets'][supplemental_revision]['observationIds'] = frame['observationIds'][:1]
    invalid['supplementalMarkets'][supplemental_revision]['frozenObservationIds'] = []
    invalid['swapMarketSelections'][supplemental_revision] = frame['observationIds'][:1]
    reject(lambda: submit(payload=generic_request, selected_frame=invalid), 'risk_job_denied')
    changed = deepcopy(generic_frame)
    changed['supplementalMarkets'][supplemental_revision]['frozenObservationIds'] = []
    reject(lambda: submit(generic_operation, generic_request, changed), 'risk_job_conflict')
    for table in ('job_markets', 'job_market_observations', 'job_market_contracts'):
        reject(lambda: query(f"\\set VERBOSITY verbose\nUPDATE risk.{table} SET catalog_id=catalog_id WHERE job_id='{generic_job['jobId']}';"), '55000')
        assert query(f"SELECT has_table_privilege('openetrm_runtime','risk.{table}','SELECT');") == 'f'
    generic_claim = json.loads(worker("SELECT risk.claim(30,'synthetic_portfolio_engine_v1')"))
    assert generic_claim['jobId'] == generic_job['jobId'] and generic_claim['frame'] == generic_frame
    for table in ('swap_trade_revisions', 'swap_instruments', 'swap_fixed_sides', 'swap_floating_sides', 'swap_reset_projections', 'swap_payment_rules'):
        assert query(f"SELECT has_table_privilege('synthetic_risk_worker','trading.{table}','SELECT') AND NOT has_table_privilege('synthetic_risk_worker','trading.{table}','INSERT,UPDATE,DELETE');") == 't'
        assert worker(f"SELECT count(*) FROM trading.{table}") == '0'
    reference_catalog, reference_revision = (manifest[key] for key in ('referenceCatalogId', 'referenceRevisionId'))
    discount = f"security.swap_discount_default('{catalog}','{reference_catalog}','{reference_revision}','usd','{supplemental_revision}','{at}')"
    worker(f"BEGIN;SELECT risk.bind('{generic_job['jobId']}','{generic_claim['leaseToken']}');SELECT {discount};COMMIT")
    reject(lambda: query(f"""\\set VERBOSITY verbose
        BEGIN;SET SESSION AUTHORIZATION synthetic_risk_worker;
        SELECT risk.bind('{generic_job['jobId']}','{generic_claim['leaseToken']}');RESET SESSION AUTHORIZATION;
        UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '0.03 seconds'
         WHERE principal_id=(SELECT principal_id FROM risk.workers WHERE login='synthetic_risk_worker');
        SET SESSION AUTHORIZATION synthetic_risk_worker;SELECT pg_sleep(0.06);SELECT {discount};ROLLBACK;"""), '42501')
    worker(f"SELECT risk.finish('{generic_job['jobId']}','{generic_claim['leaseToken']}','{{\"measure\":\"portfolio\"}}',NULL)")
    print('PASS generic risk jobs: exact supplemental frames, frozen inputs, relational pins, idempotency and read-only worker custody', flush=True)

    correction = 'a' + str(uuid4())[1:]
    query(f"""INSERT INTO market_data.observations
        SELECT catalog_id,'{correction}',definition_revision_id,series_id,reference_catalog_id,reference_revision_id,
         source_code,source_event_id,'{correction}',role,publication_state,observed_at,clock_timestamp(),value,id
        FROM market_data.observations WHERE catalog_id='{catalog}' AND id='{supplemental_frame['observationIds'][0]}';""")
    corrected = deepcopy(generic_frame)
    corrected['supplementalMarkets'][supplemental_revision]['observationIds'][0] = correction.upper()
    corrected['supplementalMarkets'][supplemental_revision]['frozenObservationIds'] = [correction.upper()]
    corrected['swapMarketSelections'][supplemental_revision][0] = correction.upper()
    reject(lambda: submit(payload=generic_request, selected_frame=corrected), 'risk_job_denied')
    later = user(f"SELECT security.capture_date_context('{catalog}','{at[:10]}',NULL)")
    corrected['contextId'] = later
    corrected['supplementalMarkets'][supplemental_revision]['contextId'] = later
    corrected_request = deepcopy(generic_request)
    corrected_request['context']['contextId'] = later
    corrected_job = submit(payload=corrected_request, selected_frame=corrected, selected_context=later)
    assert query(f"SELECT frozen FROM risk.job_market_observations WHERE job_id='{corrected_job['jobId']}' AND observation_id='{correction}';") == 't', 'UUID casing changed the retained frozen input use'
    user(f"SELECT risk.cancel('{corrected_job['jobId']}')")
    print('PASS generic risk temporal pins: later corrections require a later context and UUID casing preserves frozen input use', flush=True)

    # A part of a run made in parts (D222) states its run in its frame: the run's identity, the part and the run's part count, and every
    # job the books and portfolios its run was asked for. The store keeps one run at one retained context, for one scope, each part once,
    # whatever the API checked first. No worker claims this engine's jobs.
    run_request = {**request, 'engineVersion': 'synthetic_run_engine_v1'}
    run_id = str(uuid4())
    scope = {'bookIds': books, 'portfolioIds': []}
    def part(number, parts=2, run=None, scope=scope, run_id=run_id):
        return {**frame, 'scope': scope, 'run': run if run is not None else {'id': run_id, 'part': number, 'parts': parts}}
    for invalid in ({'id': run_id, 'part': 0, 'parts': 2}, {'id': run_id, 'part': 3, 'parts': 2}, {'id': run_id, 'part': 1, 'parts': 0},
                    {'id': run_id, 'part': 1, 'parts': 257}, {'id': run_id, 'part': 1}, {'id': run_id, 'part': 1, 'parts': 2, 'label': 'synthetic'},
                    {'id': 'synthetic', 'part': 1, 'parts': 2}, {'id': run_id, 'part': '1', 'parts': 2}, {'id': run_id, 'part': 1.5, 'parts': 2}, [], 'run', None):
        reject(lambda: submit(payload=run_request, selected_frame={**frame, 'run': invalid}), 'risk_job_run')
    first_part = submit(payload=run_request, selected_frame=part(1))
    assert first_part['run'] == {'id': run_id, 'part': 1, 'parts': 2} and first_part['scope'] == scope, 'A part states its run and the books it was asked for'
    assert first_part['shocks'] == len(run_request['bumps']), 'A job states how many shocks its request asked for'
    alone = submit(payload=run_request)
    assert alone['run'] is None and alone['scope'] is None, 'A job queued on its own states no run, and a frame without a scope states none'
    reject(lambda: submit(payload=run_request, selected_frame=part(2, scope={'bookIds': books, 'portfolioIds': [str(uuid4())]})), 'risk_job_run')
    # Every close of one date shares a context, so a part at another close of the date, its own frame, is refused as well.
    other_close = str(uuid4())
    reject(lambda: submit(payload={**run_request, 'context': {**run_request['context'], 'snapshotId': other_close}}, selected_frame={**part(2), 'id': other_close}), 'risk_job_run')
    reject(lambda: submit(payload=run_request, selected_frame=part(1)), 'risk_job_run')
    reject(lambda: submit(payload=run_request, selected_frame=part(2, parts=3)), 'risk_job_run')
    elsewhere = user(f"SELECT security.capture_date_context('{catalog}','{at[:10]}',NULL)")
    reject(lambda: submit(payload={**run_request, 'context': {**run_request['context'], 'contextId': elsewhere}},
                          selected_frame={**part(2), 'contextId': elsewhere}, selected_context=elsewhere), 'risk_job_run')
    second_part = submit(payload=run_request, selected_frame=part(2))
    listed = {row['jobId']: row['run'] for row in json.loads(user(f"SELECT risk.list('{catalog}')"))}
    assert listed[first_part['jobId']]['part'] == 1 and listed[second_part['jobId']] == {'id': run_id, 'part': 2, 'parts': 2}, 'Job history lists each part with its run'
    assert json.loads(user(f"SELECT risk.read('{second_part['jobId']}')"))['run'] == second_part['run']
    assert json.loads(user(f"SELECT risk.read('{second_part['jobId']}')"))['scope'] == scope, 'A job read states the books and portfolios its run was asked for'
    for queued in (first_part, second_part, alone):
        user(f"SELECT risk.cancel('{queued['jobId']}')")
    print('PASS risk job runs: a part states one run, its part and the part count, at one retained context and close, for one scope and once, in its retained frame', flush=True)

    if psql is not None:
        # Two sessions sending parts of one run at once are admitted one at a time: the second waits until the first commits and then sees
        # its part, so a run never holds parts at two retained contexts, or one part twice.
        def outcome(work):
            try:
                work()
                return ''
            except RuntimeError as error:
                return str(error)

        # The second session names itself, so that its wait for the run's lock can be seen before the first session commits.
        racer_name = 'synthetic_run_racer'

        def waiting():
            return query(f"SELECT count(*) FROM pg_stat_activity WHERE application_name='{racer_name}' AND wait_event_type='Lock' AND wait_event='advisory'") == '1'

        def raced(first_frame, second_sql):
            process = subprocess.Popen([str(x) for x in psql], cwd=ROOT, env=environment,
                                       stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
            result = {}
            second = lambda: query(f"SET application_name='{racer_name}';SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token()}');{second_sql};COMMIT;")
            racer = threading.Thread(target=lambda: result.update(error=outcome(second)))
            try:
                process.stdin.write(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token()}');{submit_sql(payload=run_request, selected_frame=first_frame)};\n")
                process.stdin.flush()
                held = None
                while held is None:
                    assert select.select([process.stdout], [], [], 10)[0], 'The first part was not admitted'
                    line = process.stdout.readline()
                    held = json.loads(line) if line.startswith('{') else None
                racer.start()
                deadline = time.monotonic() + 10
                waited = waiting()
                while not waited and racer.is_alive() and time.monotonic() < deadline:
                    time.sleep(0.05)
                    waited = waiting()
                assert waited, ('The second part was not seen waiting on the run\'s lock before the first part committed', result)
                process.stdin.write('COMMIT;\n\\q\n')
                process.stdin.flush()
                assert process.wait(timeout=10) == 0
                racer.join(30)
                assert not racer.is_alive() and 'risk_job_run' in result.get('error', ''), result
                return held
            finally:
                if process.poll() is None:
                    process.terminate()
                    process.wait(timeout=10)
                if racer.is_alive():
                    racer.join(30)

        racing = str(uuid4())
        held_first = raced(part(1, run_id=racing), submit_sql(payload={**run_request, 'context': {**run_request['context'], 'contextId': elsewhere}},
                                                              selected_frame={**part(2, run_id=racing), 'contextId': elsewhere}, selected_context=elsewhere))
        twice = str(uuid4())
        held_twice = raced(part(1, run_id=twice), submit_sql(payload=run_request, selected_frame=part(1, run_id=twice)))
        for queued in (held_first, held_twice):
            user(f"SELECT risk.cancel('{queued['jobId']}')")
        print('PASS risk job run concurrency: a part sent while another of its run is uncommitted waits on the run\'s lock, seen in pg_stat_activity, so no run holds parts at two contexts or a part twice', flush=True)

    operation = str(uuid4())
    reject(lambda: submit(selected_frame={**frame, 'frozenObservationIds': [str(uuid4())]}), 'risk_job_input')
    reject(lambda: submit(selected_frame={key: value for key, value in frame.items() if key != 'frozenObservationIds'}), 'risk_job_input')
    reject(lambda: submit(selected_frame={**frame, 'swapMarketSelections': {str(uuid4()): [str(uuid4())]}}), 'risk_job_input')
    reject(lambda: submit(selected_frame={**frame, 'originCloseId': str(uuid4())}), 'risk_job_denied')
    job = submit(operation)
    assert job['state'] == 'queued'
    assert job['convention'] == request['convention']
    assert submit(operation)['jobId'] == job['jobId']
    reject(lambda: submit(operation, {**request, 'detail': 'holdings'}), 'risk_job_conflict')
    reject(lambda: user(f"SELECT risk.read('{job['jobId']}')", 'trader'), 'risk_job_denied')
    assert query("SELECT has_table_privilege('openetrm_runtime','risk.jobs','SELECT');") == 'f'
    assert query("SELECT has_function_privilege('openetrm_runtime','risk.claim(integer,text)','EXECUTE');") == 'f'
    assert query("SELECT has_table_privilege('synthetic_risk_worker','market_data.observations','INSERT');") == 'f'
    assert query("SELECT has_function_privilege('synthetic_risk_worker','security.use_context(uuid)','EXECUTE');") == 'f'
    assert query("SELECT pg_has_role('synthetic_risk_worker','openetrm_runtime','MEMBER');") == 'f'
    assert query("SELECT has_table_privilege('synthetic_risk_worker','ref_data.source_documents','SELECT');") == 'f'
    assert query("SELECT has_table_privilege('synthetic_risk_worker','market_data.custom_curve_requests','SELECT');") == 'f'
    assert worker("SELECT risk.claim(30,'different_engine_v1') IS NULL") == 't'
    assert json.loads(user(f"SELECT risk.read('{job['jobId']}')"))['state'] == 'queued'
    claim = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')"))
    assert claim['jobId'] == job['jobId'] and claim['request'] == request and claim['frame'] == frame
    assert worker("SELECT risk.claim(30,'synthetic_engine_v1') IS NULL") == 't'
    reject(lambda: worker(f"SELECT risk.finish('{job['jobId']}','{uuid4()}','{{\"value\":\"9\"}}',NULL)"), 'risk_job_fence')
    reject(lambda: worker(f"SELECT risk.finish('{job['jobId']}','{claim['leaseToken']}','{{\"value\":\"9\"}}',NULL)", 'synthetic_other_worker'), 'risk_job_fence')
    bound = worker(f"BEGIN;SELECT risk.bind('{job['jobId']}','{claim['leaseToken']}');SELECT count(*) FROM market_data.revisions WHERE catalog_id='{catalog}' AND id='{revision}';COMMIT")
    assert bound.splitlines()[-1] == '1'
    # Shared metadata remains readable only in a bound current worker context.
    # PPA reference reads use the same RLS fences; raw trade heads remain private.
    ppa_guard = f"security.ppa_scope_has_positions('{catalog}',ARRAY[{','.join(repr(b)+'::uuid' for b in books)}])"
    assert worker(f"BEGIN;SELECT risk.bind('{job['jobId']}','{claim['leaseToken']}');SELECT {ppa_guard};COMMIT").splitlines()[-1] == 'f'
    reject(lambda: worker(f"SELECT {ppa_guard}"), 'No current risk worker authority')
    for table in ('generation_assets','generation_asset_versions','volume_sources','location_valuation_assignments'):
        assert query(f"SELECT has_table_privilege('synthetic_risk_worker','market_data.{table}','SELECT') AND NOT has_table_privilege('synthetic_risk_worker','market_data.{table}','INSERT,UPDATE,DELETE');") == 't'
        assert query(f"SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='market_data.{table}'::regclass;") == 't'
        assert query(f"SELECT count(*)=1 FROM pg_policies WHERE schemaname='market_data' AND tablename='{table}' AND policyname='risk_worker_service' AND permissive='RESTRICTIVE' AND roles=ARRAY['openetrm_risk_worker']::name[] AND cmd='SELECT' AND qual='risk.worker_read()';") == 't'
    assert query("SELECT has_function_privilege('synthetic_risk_worker','security.ppa_scope_heads(uuid,text,uuid[])','EXECUTE');") == 'f'
    assert query("SELECT has_table_privilege('synthetic_risk_worker','trading.ppa_trade_revisions','SELECT');") == 'f'
    # Roll back a deliberately injected row: this tests nonempty RLS visibility,
    # not eligibility of this legacy fixture curve as an executable frozen anchor.
    expiry_setup = f"""BEGIN;ALTER TABLE market_data.curve_expiry_rules DISABLE TRIGGER guard_definition;
    INSERT INTO market_data.curve_expiry_rules VALUES('{catalog}','{revision}','{curve['code']}','freeze_final_settlement_v1');
    SET SESSION AUTHORIZATION synthetic_risk_worker;SELECT risk.bind('{job['jobId']}','{claim['leaseToken']}');"""
    expiry_read = f"SELECT count(*) FROM market_data.curve_expiry_rules WHERE catalog_id='{catalog}' AND revision_id='{revision}'"
    assert query(expiry_setup+expiry_read+';ROLLBACK;').splitlines()[-1] == '1'
    assert worker(expiry_read).splitlines()[-1] == '0'
    expiry_loss = """RESET SESSION AUTHORIZATION;
    UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '0.03 seconds'
     WHERE principal_id=(SELECT principal_id FROM risk.workers WHERE login='synthetic_risk_worker');
    SET SESSION AUTHORIZATION synthetic_risk_worker;SELECT pg_sleep(0.06);"""
    assert query(expiry_setup+expiry_loss+expiry_read+';ROLLBACK;').splitlines()[-1] == '0'
    reject(lambda: query(expiry_setup+expiry_loss+f'SELECT {ppa_guard};ROLLBACK;'), 'No current risk worker authority')
    print('PASS risk PPA boundary: fenced read-only reference grants, nonempty expiry metadata, service loss and no raw trade reads',flush=True)
    assert worker(f"SELECT count(*) FROM market_data.revisions WHERE catalog_id='{catalog}'") == '0'
    expired_service = query(f"""BEGIN;SET SESSION AUTHORIZATION synthetic_risk_worker;
    SELECT risk.bind('{job['jobId']}','{claim['leaseToken']}');RESET SESSION AUTHORIZATION;
    UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '0.03 seconds'
     WHERE principal_id=(SELECT principal_id FROM risk.workers WHERE login='synthetic_risk_worker');
    SET SESSION AUTHORIZATION synthetic_risk_worker;SELECT pg_sleep(0.06);
    SELECT count(*) FROM market_data.revisions WHERE catalog_id='{catalog}';ROLLBACK;""")
    assert expired_service.splitlines()[-1] == '0'
    assert worker(f"SELECT risk.heartbeat('{job['jobId']}','{claim['leaseToken']}',30)") == 'running'
    worker(f"SELECT risk.finish('{job['jobId']}','{claim['leaseToken']}','{{\"value\":\"9\"}}',NULL)")
    completed = json.loads(user(f"SELECT risk.read('{job['jobId']}')"))
    assert completed['state'] == 'completed' and completed['result'] == {'value': '9'}
    assert json.loads(user(f"SELECT risk.cancel('{job['jobId']}')"))['state'] == 'completed'
    reject(lambda: worker(f"SELECT risk.finish('{job['jobId']}','{claim['leaseToken']}','{{}}',NULL)"), 'risk_job_fence')
    print('PASS risk jobs: exact captured inputs, idempotency, private ownership, transaction-local reads and single terminal publication', flush=True)

    # Delay only response construction inside a rolled-back fixture. Independent
    # ReadTrades authority expires while RunScenario and market-read rights stay
    # live, so a final scenario-only check cannot satisfy this contract.
    extra = submit()
    user(f"SELECT risk.cancel('{extra['jobId']}')")
    for operation_sql in (f"risk.read('{job['jobId']}')", f"risk.list('{catalog}')"):
        remaining_role = str(uuid4())
        delayed = f"""BEGIN;
        INSERT INTO security.roles VALUES('{remaining_role}','synthetic_risk_read_expiry','Synthetic risk read expiry');
        INSERT INTO security.role_permissions VALUES('{remaining_role}','ReadMarketData'),('{remaining_role}','RunScenario');
        INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,'{remaining_role}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false
         FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='risk';
        ALTER FUNCTION risk.summary(risk.jobs) RENAME TO summary_before_expiry_probe;
        CREATE FUNCTION risk.summary(job risk.jobs) RETURNS jsonb LANGUAGE plpgsql SET search_path=pg_catalog AS $$
        BEGIN
         IF current_setting('synthetic_risk.summary_delayed',true) IS DISTINCT FROM 'true' THEN
          IF NOT security.has_permission('ReadTrades') THEN RAISE EXCEPTION 'expiry_probe_not_admitted';END IF;
          PERFORM set_config('synthetic_risk.summary_delayed','true',true);
          PERFORM pg_sleep(0.35);
          RAISE NOTICE 'synthetic_risk_summary_assembled';
         END IF;
         RETURN risk.summary_before_expiry_probe(job);
        END$$;
        UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '0.25 seconds'
         WHERE principal_id=(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='risk')
          AND role_id<>'{remaining_role}';
        SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token()}');
        SELECT {operation_sql};ROLLBACK;"""
        try:
            query(delayed)
            raise AssertionError('Risk read published after independent ReadTrades authority expired')
        except RuntimeError as error:
            assert 'risk_job_denied' in str(error) and 'synthetic_risk_summary_assembled' in str(error), str(error)
    print('PASS risk reads: result and list assembly recheck independently expiring trade-read authority', flush=True)

    if psql is not None:
        first, second = submit(), submit()
        process = subprocess.Popen([str(x) for x in psql], cwd=ROOT, env=environment,
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            process.stdin.write("SET SESSION AUTHORIZATION synthetic_risk_worker;BEGIN;SELECT risk.claim(30,'synthetic_engine_v1');\n")
            process.stdin.flush()
            assert select.select([process.stdout], [], [], 10)[0], 'First claim did not become ready'
            claimed_first = json.loads(process.stdout.readline())
            claimed_second = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')", 'synthetic_other_worker'))
            assert claimed_first['jobId'] == first['jobId'] and claimed_second['jobId'] == second['jobId']
            process.stdin.write('COMMIT;\n\\q\n')
            process.stdin.flush()
            assert process.wait(timeout=10) == 0
            worker(f"SELECT risk.finish('{first['jobId']}','{claimed_first['leaseToken']}',NULL,'synthetic_failure')")
            worker(f"SELECT risk.finish('{second['jobId']}','{claimed_second['leaseToken']}',NULL,'synthetic_failure')", 'synthetic_other_worker')
        finally:
            if process.poll() is None:
                process.terminate()
                process.wait(timeout=10)
        print('PASS risk worker concurrency: an uncommitted claim locks only its own job', flush=True)

    queued = submit()
    assert json.loads(user(f"SELECT risk.cancel('{queued['jobId']}')"))['state'] == 'cancelled'
    running = submit()
    active = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')"))
    assert active['jobId'] == running['jobId']
    assert json.loads(user(f"SELECT risk.cancel('{running['jobId']}')"))['state'] == 'cancellation_requested'
    assert worker(f"SELECT risk.heartbeat('{running['jobId']}','{active['leaseToken']}',30)") == 'cancellation_requested'
    assert worker(f"SELECT risk.finish('{running['jobId']}','{active['leaseToken']}','{{}}',NULL)") == 'cancellation_requested'
    assert worker(f"SELECT risk.acknowledge_cancel('{running['jobId']}','{active['leaseToken']}')") == 'cancelled'
    delayed = submit()
    delayed_claim = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')"))
    user(f"SELECT risk.cancel('{delayed['jobId']}')")
    query(f"UPDATE risk.jobs SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id='{delayed['jobId']}';")
    assert worker("SELECT risk.claim(30,'synthetic_engine_v1') IS NULL") == 't'
    assert json.loads(user(f"SELECT risk.read('{delayed['jobId']}')"))['state'] == 'cancellation_requested'
    reject(lambda: worker(f"SELECT risk.finish('{delayed['jobId']}','{delayed_claim['leaseToken']}','{{}}',NULL)"), 'risk_job_fence')
    assert worker(f"SELECT risk.acknowledge_cancel('{delayed['jobId']}','{delayed_claim['leaseToken']}')") == 'cancelled'
    print('PASS risk cancellation: queued cancellation, worker acknowledgment and cancellation-first publication rejection', flush=True)

    interrupted = submit()
    old = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')"))
    query(f"UPDATE risk.jobs SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id='{interrupted['jobId']}';")
    new = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')", 'synthetic_other_worker'))
    assert new['jobId'] == old['jobId'] and new['leaseToken'] != old['leaseToken'] and new['attempt'] == old['attempt'] + 1
    assert new['frame'] == old['frame'] and new['request'] == old['request']
    reject(lambda: worker(f"SELECT risk.finish('{old['jobId']}','{old['leaseToken']}','{{}}',NULL)"), 'risk_job_fence')
    worker(f"SELECT risk.finish('{new['jobId']}','{new['leaseToken']}',NULL,'synthetic_failure')", 'synthetic_other_worker')
    assert json.loads(user(f"SELECT risk.read('{new['jobId']}')"))['state'] == 'failed'
    print('PASS risk recovery: expired worker ownership retries identical pins and stale workers cannot publish', flush=True)

    oversized = submit()
    oversized_claim = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')"))
    reject(lambda: worker(f"SELECT risk.finish('{oversized['jobId']}','{oversized_claim['leaseToken']}',jsonb_build_object('payload',repeat('x',32000001)),NULL)"), 'risk_result_limit')
    worker(f"SELECT risk.finish('{oversized['jobId']}','{oversized_claim['leaseToken']}',NULL,'risk_result_limit')")
    oversized_result = json.loads(user(f"SELECT risk.read('{oversized['jobId']}')"))
    assert oversized_result['state'] == 'failed' and oversized_result['errorCode'] == 'risk_result_limit' and oversized_result['result'] is None
    assert worker("SELECT risk.claim(30,'synthetic_engine_v1') IS NULL") == 't'
    print('PASS risk result bounds: oversized output rejects and becomes a terminal structured failure without retrying', flush=True)

    revoked = submit()
    proof = json.loads(worker("SELECT risk.claim(30,'synthetic_engine_v1')"))
    actor = query("SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='risk';")
    query(f"UPDATE security.workspace_memberships SET enabled=false WHERE principal_id='{actor}';UPDATE security.workspace_memberships SET enabled=true WHERE principal_id='{actor}';")
    reject(lambda: worker(f"SELECT risk.finish('{revoked['jobId']}','{proof['leaseToken']}','{{}}',NULL)"), 'risk_job_authority_changed')
    query(f"UPDATE risk.jobs SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id='{revoked['jobId']}';")
    assert worker("SELECT risk.claim(30,'synthetic_engine_v1') IS NULL") == 't'
    assert json.loads(user(f"SELECT risk.read('{revoked['jobId']}')"))['state'] == 'failed'
    fresh = submit()
    query(f"UPDATE security.workspace_memberships SET enabled=false WHERE principal_id='{actor}';")
    reject(lambda: user(f"SELECT risk.read('{fresh['jobId']}')"), 'risk_job_denied')
    assert worker("SELECT risk.claim(30,'synthetic_engine_v1') IS NULL") == 't'
    query(f"UPDATE security.workspace_memberships SET enabled=true WHERE principal_id='{actor}';")
    print('PASS risk authorization: queued and running revoke/restore invalidation, current result denial and no authority revival', flush=True)

    user(f"""SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{at}');
        DO $$DECLARE receipt jsonb;BEGIN FOR n IN 1..51 LOOP
         receipt=risk.submit('{catalog}',gen_random_uuid(),'{context}','{revision}','{at}',{literal(request)},{literal(frame)});
         PERFORM risk.cancel((receipt->>'jobId')::uuid);
        END LOOP;END$$""")
    recent = json.loads(user(f"SELECT risk.list('{catalog}')"))
    generic_history = json.loads(user(f"SELECT risk.list('{catalog}','portfolio_instrument_repricing_v1')"))
    assert len(recent) == 50 and all(row['convention'] == request['convention'] for row in recent)
    assert recent == generic_history
    assert all(row['convention'] == generic_request['convention'] for row in generic_history)
    print('PASS risk history: the single connected operation retains a bounded recent-job list', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--pg-bin', type=Path, required=True)
    args = parser.parse_args()
    bins = {name: args.pg_bin.resolve() / name for name in ('initdb', 'pg_ctl', 'psql')}
    for binary in bins.values():
        if not binary.is_file():
            parser.error(f'Missing installed binary: {binary}')
    environment = {k: v for k, v in os.environ.items() if not k.startswith('PG')}
    environment['PGTZ'] = 'UTC'
    directory = Path(tempfile.mkdtemp(prefix='openetrm-risk-db-', dir='/tmp')).resolve()
    data = directory / 'data'
    startup_attempted = False
    safe_to_remove = True
    def run(command, **kwargs):
        return subprocess.run([str(x) for x in command], cwd=ROOT, env=environment, text=True, capture_output=True, timeout=30, **kwargs)
    try:
        result = run([bins['initdb'], '-D', data, '--auth-local=trust', '--auth-host=reject', '--encoding=UTF8', '--locale=C', '--no-instructions', '--username=preview_owner'])
        if result.returncode:
            raise RuntimeError(result.stderr)
        startup_attempted, safe_to_remove = True, False
        result = run([bins['pg_ctl'], '-D', data, '-w', '-t', '15', '-l', directory / 'postgres.log', '-o', shlex.join(['-k', str(directory), '-h', '', '-p', '55439', '-c', 'track_commit_timestamp=on']), 'start'])
        if result.returncode:
            raise RuntimeError(result.stderr)
        psql = [bins['psql'], '-X', '-h', directory, '-p', '55439', '-U', 'preview_owner', '-d', 'postgres', '-qAt', '-v', 'ON_ERROR_STOP=1']
        def query(sql):
            result = run(psql, input=sql)
            if result.returncode:
                raise RuntimeError(result.stderr)
            return result.stdout.strip()
        assert Path(query('SHOW data_directory;')).resolve() == data
        for path in sorted((ROOT / 'db/migrations').glob('[0-9][0-9][0-9][0-9]_*.sql')):
            query(path.read_text())
        reference, market, security, manifest = fixture(ROOT)
        from swap_fixture import fixture as swap_fixture
        swap_market, swap_manifest = swap_fixture()
        manifest['swapMarkets'] = [swap_manifest]
        for sql in (reference, market, swap_market, security):
            query(sql)
        query(trading_fixture(manifest['catalogId'], swap_defaults=True))
        run_checks(query, manifest, psql, environment)
    finally:
        if startup_attempted:
            stopped = run([bins['pg_ctl'], '-D', data, '-m', 'immediate', '-w', '-t', '15', 'stop'])
            safe_to_remove = stopped.returncode == 0 or not (data / 'postmaster.pid').exists()
        if safe_to_remove:
            shutil.rmtree(directory)
        else:
            print(f'Owned cluster retained because shutdown is unconfirmed: {directory}')


if __name__ == '__main__':
    main()
