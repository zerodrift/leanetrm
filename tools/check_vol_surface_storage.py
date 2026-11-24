"""Stored volatility surfaces in the owned exchange example: immutable revisions recorded once per request and per
valuation instant and pinned input set (D238) at one close or working snapshot (D356), published through the trading
knowledge protocol and read at a certified cutoff."""
import hashlib
import json
import threading
import time
from uuid import uuid4

MODEL = 'smile_quadratic_log_moneyness_v1'
POLICY = 'american_futures_option_crr_v1'
FORMULA = 'european_futures_option_black76_v1'
RECORD = 'trading.record_vol_surface_revision(uuid,uuid,uuid,jsonb,date,text,text,uuid,uuid,timestamptz,uuid[],jsonb,text)'


def run_checks(query, manifest):
    catalog = manifest['catalogId']
    frame_at = manifest['frames'][0]['at']
    day = frame_at[:10]
    # Another valuation instant on the same business date.
    later_at = day + 'T17:00:00Z'
    product = json.dumps({'referenceCatalogId': manifest['referenceCatalogId'], 'referenceRevisionId': manifest['referenceRevisionId'], 'code': 'nymex_on'})
    other_product = product.replace('nymex_on', 'nymex_lo')
    actor = query(f"SELECT principal_id FROM security.identity_bindings WHERE issuer='{manifest['identityIssuer']}' AND subject='trader';")
    observations = query(f"SELECT string_agg(id::text,',' ORDER BY id) FROM(SELECT id FROM market_data.observations WHERE catalog_id='{catalog}' AND observed_at<='{frame_at}' ORDER BY id LIMIT 3)x;").split(',')
    first_inputs, second_inputs = observations[:2], observations[1:]
    # A stored surface states the sensitivity selection it was computed under; these two share one.
    selection = {'policy': 'synthetic_selection'}
    first_surface, second_surface = {'synthetic': 'first fit', 'sensitivities': selection}, {'synthetic': 'second fit', 'sensitivities': selection}
    first_frame = str(uuid4())

    def check(name, condition, actual=None):
        assert condition, f'{name}: {actual}'
        print('PASS ' + name, flush=True)

    def digest(surface):
        return hashlib.sha256(json.dumps(surface, sort_keys=True).encode()).hexdigest()

    def record(request, inputs, surface, sha=None, frame=first_frame, close=None, valuation=frame_at, model=MODEL, null=None, policy=POLICY, key=None):
        """The internal recording a risk worker's finish path makes, run here as the owning connection, at a working
        snapshot unless a close is named; `null` names one argument passed as SQL NULL."""
        arguments = {'catalog': f"'{catalog}'", 'actor': f"'{actor}'", 'request': f"'{request}'", 'product': f"'{key or product}'::jsonb",
                     'business_date': f"'{day}'", 'model': f"'{model}'", 'policy': f"'{policy}'", 'frame': f"'{frame}'",
                     'close': f"'{close}'" if close else 'NULL',
                     'valuation_at': f"'{valuation}'::timestamptz",
                     'inputs': 'ARRAY[' + ','.join(f"'{o}'" for o in inputs) + ']::uuid[]', 'surface': f"'{json.dumps(surface)}'::jsonb",
                     'digest': f"'{sha or digest(surface)}'"}
        if null is not None:
            arguments[null] = 'NULL'
        return f"SELECT trading.record_vol_surface_revision({','.join(arguments.values())})"

    def rejection(body, code, state='P0001'):
        """A block that fails unless body raises this SQLSTATE (and message)."""
        guard = '' if code is None else f"IF SQLERRM<>'{code}' THEN RAISE;END IF;"
        return f"DO $$ BEGIN {body};RAISE EXCEPTION 'Expected rejection';EXCEPTION WHEN SQLSTATE '{state}' THEN {guard}END $$;"

    def refused(statement, code, state='P0001'):
        return query(f"BEGIN;{rejection('PERFORM ' + statement.removeprefix('SELECT '), code, state)}ROLLBACK;") is not None

    def counts():
        return query(f"SELECT (SELECT count(*) FROM trading.vol_surface_revisions WHERE catalog_id='{catalog}')||'|'||"
                     f"(SELECT count(*) FROM trading.publications WHERE catalog_id='{catalog}');")

    def token():
        return query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                     f"'{manifest['identityIssuer']}','trader','human_interactive','{manifest['tenantId']}','{manifest['workspaceId']}',NULL,'preview_runtime',60);")

    def trader(statements, end='COMMIT'):
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token()}');{statements}{end};")

    def capture():
        return trader(f"SELECT security.resolve_operation_context('{catalog}','{day}',NULL);")

    def bound(context):
        return f"SELECT security.use_market_context('{catalog}','{context}');SELECT security.use_trading_context('{catalog}','{frame_at}');"

    def reading(key=None, date=None, model=MODEL, close=None, valuation=frame_at, pins=observations):
        """A read at a close, or at a working snapshot when none is named, a valuation instant and the frame's pins."""
        located = f"'{close}'" if close else 'NULL'
        pinned = 'ARRAY[' + ','.join(f"'{o}'" for o in pins) + ']::uuid[]'
        return f"security.read_vol_surface('{catalog}','{manifest['revisionId']}','{key or product}'::jsonb,'{date or day}','{model}',{located}::uuid,'{valuation}'::timestamptz,{pinned})"

    def read(context, **key):
        return trader(bound(context) + f"SELECT {reading(**key)};", 'ROLLBACK')

    def read_refused(context, code, state='P0001', setup='', **key):
        return trader(bound(context) + setup + rejection('PERFORM ' + reading(**key), code, state), 'ROLLBACK') is not None

    before = counts()

    # A revision is recorded under the numerical policy of its product's exercise style, and under no other (D449). The
    # example's products are American and its reference rows are sealed, so the second product is stated as European for
    # one rolled-back transaction, with the sealed rows' guards set aside for that one change only.
    def as_european(statement):
        return ("SET LOCAL session_replication_role=replica;UPDATE ref_data.futures_option_products SET exercise_style='european' "
                f"WHERE(catalog_id,revision_id,code)=('{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}','nymex_lo');"
                f"SET LOCAL session_replication_role=origin;{statement}")
    check('an American product\'s revision is refused under the formula\'s policy',
          refused(record(str(uuid4()), first_inputs, first_surface, policy=FORMULA), 'vol_surface_input'))
    european = query('BEGIN;' + as_european(record(str(uuid4()), first_inputs, first_surface, policy=FORMULA, key=other_product)) + ';ROLLBACK;')
    check('a European product\'s revision is recorded under the formula\'s policy', json.loads(european.splitlines()[0]).get('revision'), european)
    check('a European product\'s revision is refused under the tree\'s policy', query('BEGIN;' + as_european(rejection(
        'PERFORM ' + record(str(uuid4()), first_inputs, first_surface, key=other_product).removeprefix('SELECT '), 'vol_surface_input')) + 'ROLLBACK;') is not None)
    check('the stored table admits the two solver policies and no other', query(
        "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conrelid='trading.vol_surface_revisions'::regclass AND conname='vol_surface_revisions_solver_policy_check';")
        .count('_futures_option_') == 2)
    check('the rolled-back probes stored nothing', counts() == before, counts())

    # Two connections fit the same key from the same pinned inputs: the publication lock serializes them on the key.
    results = {}

    def session(name, request, hold):
        results[name] = json.loads(query(f"BEGIN;{record(request, first_inputs, first_surface)};{hold}COMMIT;").splitlines()[0])
    first_request = str(uuid4())
    holder = threading.Thread(target=session, args=('first', first_request, 'SELECT pg_sleep(2);'))
    holder.start()
    deadline = time.monotonic() + 10
    while query("SELECT count(*) FROM pg_locks WHERE locktype='advisory' AND objid=730849102 AND granted;") == '0':
        assert time.monotonic() < deadline and holder.is_alive()
        time.sleep(.05)
    session('second', str(uuid4()), '')
    holder.join()
    first = results['first']
    check('concurrent identical fits record one revision', first['created'] and not results['second']['created']
          and results['second']['revision'] == first['revision'] and counts() == f"{int(before.split('|')[0]) + 1}|{int(before.split('|')[1]) + 1}",
          (results, counts()))
    stored = query(f"SELECT concat_ws('|',actor_id,request_id,effective_on,model_id,solver_policy,valuation_at=timestamptz '{frame_at}',content_sha256,publication_order) "
                   f"FROM trading.vol_surface_revisions WHERE (catalog_id,id)=('{catalog}','{first['revision']}');")
    check('the revision keeps its actor, request, business date, model, solver policy, valuation instant, digest and publication order',
          stored == f"{actor}|{first_request}|{day}|{MODEL}|{POLICY}|t|{digest(first_surface)}|{first['publicationOrder']}", stored)
    check('the revision pins exactly its input observations',
          query(f"SELECT string_agg(observation_id::text,',' ORDER BY observation_id) FROM trading.vol_surface_inputs WHERE (catalog_id,revision_id)=('{catalog}','{first['revision']}');")
          == ','.join(first_inputs))

    retried = query(record(first_request, first_inputs, first_surface) + ';')
    check('a retried request returns its first outcome', json.loads(retried) == first and counts().split('|')[0] == str(int(before.split('|')[0]) + 1), retried)
    check('the same request with a different payload is refused',
          refused(record(first_request, first_inputs, second_surface), 'vol_surface_input')
          and refused(record(first_request, second_inputs, first_surface), 'vol_surface_input')
          and refused(record(first_request, first_inputs, first_surface, frame=uuid4()), 'vol_surface_input')
          and refused(record(first_request, first_inputs, first_surface, valuation=later_at), 'vol_surface_input'))
    settled = counts()
    again = json.loads(query(record(uuid4(), list(reversed(first_inputs)), first_surface, frame=uuid4()) + ';'))
    check('identical inputs and content record nothing new',
          again == {**first, 'created': False} and counts() == settled, (again, counts()))
    check('identical inputs with different content are refused as nondeterministic',
          refused(record(uuid4(), first_inputs, second_surface), 'vol_surface_nondeterministic')
          and refused(record(uuid4(), first_inputs, first_surface, sha=digest(second_surface)), 'vol_surface_nondeterministic') and counts() == settled)
    # Time to expiry runs from the valuation instant, so the same observations valued at another instant are other inputs.
    moved = json.loads(query(record(uuid4(), first_inputs, second_surface, valuation=later_at) + ';'))
    check('the same observations at another valuation instant record a new revision',
          moved['created'] and moved['revision'] != first['revision'] and moved['publicationOrder'] > first['publicationOrder']
          and counts() == f"{int(settled.split('|')[0]) + 1}|{int(settled.split('|')[1]) + 1}", (moved, counts()))
    settled = counts()
    check('at that instant, the same observations with different content are refused as nondeterministic',
          refused(record(uuid4(), first_inputs, first_surface, valuation=later_at), 'vol_surface_nondeterministic') and counts() == settled)
    # The no-op test compares the latest revision at the same instant, not the latest of the day.
    refit = json.loads(query(record(uuid4(), first_inputs, first_surface) + ';'))
    check('a refit at an earlier instant of the day, after a later instant was fitted, records nothing new',
          refit == {**first, 'created': False} and counts() == settled
          and refused(record(uuid4(), first_inputs, second_surface), 'vol_surface_nondeterministic'), (refit, counts()))
    for table in ('vol_surface_revisions', 'vol_surface_inputs', 'vol_surface_commands'):
        check(f'{table} rows are immutable', all(query(f"BEGIN;{rejection(change, None, '55000')}ROLLBACK;") is not None
                                                 for change in (f'UPDATE trading.{table} SET catalog_id=catalog_id', f'DELETE FROM trading.{table}')))
    check('malformed recordings are structured errors',
          refused(record(uuid4(), [], first_surface), 'vol_surface_input')
          and refused(record(uuid4(), first_inputs + first_inputs[:1], first_surface), 'vol_surface_input')
          and refused(record(uuid4(), [str(uuid4())], first_surface), 'vol_surface_input')
          and refused(record(uuid4(), first_inputs, first_surface, sha='not-a-digest'), 'vol_surface_input')
          and refused(record(uuid4(), first_inputs, first_surface, valuation='infinity'), 'vol_surface_input')
          and refused(record(uuid4(), first_inputs, first_surface, model='smile_unknown_v1'), 'vol_model_unknown')
          and refused(record(uuid4(), first_inputs, first_surface).replace('nymex_on', 'nymex_unlisted'), 'vol_surface_product')
          and refused(record(uuid4(), first_inputs, first_surface, close=uuid4()), 'vol_surface_input'))
    null_codes = {'product': 'vol_surface_product', 'model': 'vol_model_unknown'}
    # A null close is a working snapshot's, so it is not among the refused nulls.
    for argument in ('catalog', 'actor', 'request', 'product', 'business_date', 'model', 'policy', 'frame', 'valuation_at', 'inputs', 'surface', 'digest'):
        check(f'a recording with a null {argument} is refused', refused(record(uuid4(), first_inputs, first_surface, null=argument),
                                                                        null_codes.get(argument, 'vol_surface_input')))
    check('no runtime, worker or authenticator role can record directly',
          query("SELECT string_agg(r||':'||has_function_privilege(r,'" + RECORD + "','EXECUTE'),',' ORDER BY r) FROM unnest(ARRAY['openetrm_authenticator','openetrm_risk_worker','openetrm_runtime'])r;")
          == 'openetrm_authenticator:false,openetrm_risk_worker:false,openetrm_runtime:false'
          and trader(rejection('PERFORM ' + record(uuid4(), first_inputs, first_surface).removeprefix('SELECT '), None, '42501')
                     + rejection('PERFORM 1 FROM trading.vol_surface_revisions', None, '42501'), 'ROLLBACK') is not None)

    earlier = capture()
    second = json.loads(query(record(uuid4(), second_inputs, second_surface) + ';'))
    check('new inputs record a new revision with a later publication order',
          second['created'] and second['revision'] != first['revision'] and second['publicationOrder'] > first['publicationOrder'], second)
    later = capture()
    seen_earlier, seen_later = json.loads(read(earlier)), json.loads(read(later))
    check('earlier cutoff does not see later revision', seen_earlier['revision'] == first['revision'] and seen_later['revision'] == second['revision'],
          (seen_earlier, seen_later))
    check('a read names its valuation instant: another instant of the day reads that instant\'s revision',
          json.loads(read(later, valuation=later_at))['revision'] == moved['revision'], read(later, valuation=later_at))
    check('a read returns the revision with its inputs, valuation instant, knowledge time, actor and content',
          seen_later['inputs'] == second_inputs and seen_later['actor'] == actor and seen_later['content'] == second_surface
          and seen_later['valuationAt'].replace('+00:00', 'Z') == frame_at
          and seen_later['knownFrom'] is not None and seen_earlier['knownFrom'] <= seen_later['knownFrom']
          and (seen_later['model'], seen_later['solverPolicy'], seen_later['effectiveOn']) == (MODEL, POLICY, day), seen_later)
    check('the fit job read accepts the same exact location with no job yet',
          trader(bound(later) + f"SELECT {reading().replace('security.read_vol_surface', 'risk.vol_surface_fit_job')};",
                 'ROLLBACK') == '')
    check('a product never fitted for the date reads as not fitted', read(later, key=other_product) == '')
    check('a frame that does not pin every input of the latest revision reads the latest one it does pin',
          json.loads(read(later, pins=first_inputs))['revision'] == first['revision'] and read(later, pins=observations[:1]) == '',
          read(later, pins=first_inputs))
    check('a working snapshot must supply distinct non-null pins from the selected source',
          trader(bound(later) + f"SELECT {reading().replace(']::uuid[]', ',NULL]::uuid[]')};", 'ROLLBACK') == ''
          and read(later, pins=observations + observations[:1]) == ''
          and read(later, pins=observations + [str(uuid4())]) == '')
    check('a read for another business date than its context is refused', read_refused(later, 'vol_surface_context', date='2026-10-15'))
    check('a read for an unknown model is refused', read_refused(later, 'vol_model_unknown', model='smile_unknown_v1'))
    check('a read without a trading context is refused',
          trader(rejection('PERFORM ' + reading(), 'knowledge_coverage'), 'ROLLBACK') is not None)

    def withdrawn(table):
        return f"RESET SESSION AUTHORIZATION;DELETE FROM security.{table};SET SESSION AUTHORIZATION preview_runtime;"
    check('reading requires ReadMarketData', read_refused(later, None, '42501', setup=withdrawn(f"market_catalog_scopes WHERE catalog_id='{catalog}'")))
    check('a product whose reference data the reader cannot read is refused exactly as an unknown one',
          read_refused(later, 'vol_surface_product', setup=withdrawn(f"reference_catalog_scopes WHERE catalog_id='{manifest['referenceCatalogId']}'")))
    check('nothing was recorded by the refused and rolled-back checks', counts() == f"{int(settled.split('|')[0]) + 1}|{int(settled.split('|')[1]) + 1}", counts())

    # The selection is an input: the same observations under another selection are another fact, not a conflicting refit.
    check('a surface that states no sensitivity selection is refused',
          refused(record(uuid4(), first_inputs, {'synthetic': 'first fit'}), 'vol_surface_input')
          and refused(record(uuid4(), first_inputs, {'synthetic': 'first fit', 'sensitivities': 'synthetic_selection'}), 'vol_surface_input'))
    settled = counts()
    other_surface = {'synthetic': 'first fit', 'sensitivities': {'policy': 'synthetic_other_selection'}}
    reselected = json.loads(query(record(uuid4(), first_inputs, other_surface) + ';'))
    newest = capture()
    check('the same inputs under another sensitivity selection record a new revision, which a later cutoff reads and an earlier one does not',
          reselected['created'] and reselected['revision'] != first['revision'] and reselected['publicationOrder'] > first['publicationOrder']
          and counts() == f"{int(settled.split('|')[0]) + 1}|{int(settled.split('|')[1]) + 1}"
          and json.loads(read(newest, pins=first_inputs))['revision'] == reselected['revision']
          and json.loads(read(later, pins=first_inputs))['revision'] == first['revision'], (reselected, counts(), read(newest, pins=first_inputs)))
    settled = counts()
    # Each selection's latest revision at this instant is what its refit is compared with: the first selection's is the second fit.
    unchanged = json.loads(query(record(uuid4(), second_inputs, second_surface) + ';'))
    check('each selection keeps its own latest revision: a refit under either records nothing new, and other content under either is nondeterministic',
          unchanged == {**second, 'created': False, 'knownFrom': unchanged['knownFrom']}
          and json.loads(query(record(uuid4(), first_inputs, other_surface) + ';'))['revision'] == reselected['revision'] and counts() == settled
          and refused(record(uuid4(), first_inputs, {**other_surface, 'synthetic': 'changed'}), 'vol_surface_nondeterministic')
          and refused(record(uuid4(), second_inputs, {**second_surface, 'synthetic': 'changed'}), 'vol_surface_nondeterministic') and counts() == settled,
          (unchanged, counts()))
    # Populated transition: the same pins and valuation instant under the retained
    # axis-input policy create a new revision without mutating prior content.
    axis_surface={**second_surface,'plotInputs':{'axisNumericalPolicy':'vol_surface_axes_decimal_v1','normalImplementation':'normal_decimal_taylor_bisection_v1'}}
    axis_revision=json.loads(query(record(uuid4(),second_inputs,axis_surface)+';'))
    check('explicit axis-input refit preserves legacy history and records one new policy revision',
          axis_revision['created'] and axis_revision['revision']!=second['revision'] and
          json.loads(query(record(uuid4(),second_inputs,axis_surface)+';'))['revision']==axis_revision['revision'] and
          json.loads(query(record(uuid4(),second_inputs,second_surface)+';'))['revision']==second['revision'] and
          refused(record(uuid4(),second_inputs,{**axis_surface,'synthetic':'different'}),'vol_surface_nondeterministic'))
    check('the Options page is a page whose guidance can be configured', query("SELECT assistant.is_prompt_page('options') AND NOT assistant.is_prompt_page('option');") == 't')
