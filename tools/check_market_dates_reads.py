"""Statement scaling and disclosure boundaries in the launcher's disposable database."""
from copy import deepcopy
from contextlib import contextmanager
from datetime import date, timedelta
from pathlib import Path
import json
import hashlib
import platform
import os
import re
import threading
import time
from uuid import uuid4
from assistant_context import holding
from preview_client import PreviewClient
from seed_session_closes import publish_session, record_close
from check_market_dates import visibility_probe

SESSION = 'synthetic_calendar_scaling'
# Business projections captured from unchanged cb582382 in this exact synthetic fixture.
EXPECTED = {'curve': 'f899a197032bd27475da3adee6f027f3be06778230ca80405e18f1087153ba63', 'denied': '4292f04ecba1c37badc016fc5649e1131f4737bf9fca2ef2d44ce615f7af1d65', 'hidden': 'c40959aaece96f7549b10036de77257a0682d1793a40b7d9a31cbc66b5019baf', 'rate': '67399245cd08d8a1f0b07d00827e581fe398d136af30a46d915ead808e35ee0d', 'replaced': 'd444694f6c3a9de28159180e0ccd77d9f622f11cb091944c69249410c82967e5', 'retained': 'e88ca9402cd8c6d63bfefa0d89d6971fa261b8253e1c124e2e2becdc0486611b', 'workspace': '640376bb23df1cf40d66d4ceac651a820d7729c68d2ff60cb143c0bd0d3d442b'}


def require_constant_statements(counts):
    assert len(set(counts.values())) == 1 and min(counts.values()) > 0, f'Statements grow with unselected closes: {counts}'


def projection(answer):
    """Keep business answers; random fixture identities and capture instants vary per run."""
    value = deepcopy(answer)
    value.pop('frameCatalog', None)
    if value.get('context'):
        value['context'] = {'effectiveOn': value['context']['effectiveOn']}
    for day in value.get('dates', []):
        for frame in day['frames']:
            frame.pop('id')
    for curve in value.get('curves', []):
        curve.pop('id')
    return value


@contextmanager
def original_read_probe(query):
    """Identify the materializing identity transaction from its native point reads."""
    signature = 'security.read_observation_pins(uuid,uuid,uuid[])'
    pins = query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    query("""CREATE TABLE public.synthetic_calendar_read_context(id uuid PRIMARY KEY);
      CREATE FUNCTION public.synthetic_calendar_read_finished() RETURNS void LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
       INSERT INTO public.synthetic_calendar_read_context SELECT id FROM security.request_contexts
        WHERE backend_pid=pg_backend_pid() AND transaction_id=pg_current_xact_id_if_assigned() AND runtime_login=session_user ON CONFLICT DO NOTHING;
      $$;
      CREATE FUNCTION public.synthetic_calendar_source_visible() RETURNS boolean LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS $$
       SELECT NOT EXISTS(SELECT 1 FROM public.synthetic_calendar_read_context saved WHERE NOT EXISTS(
        SELECT 1 FROM security.request_contexts c WHERE c.id=saved.id AND c.backend_pid=pg_backend_pid()
         AND c.transaction_id=pg_current_xact_id_if_assigned() AND c.runtime_login=session_user));
      $$;
      REVOKE ALL ON FUNCTION public.synthetic_calendar_read_finished(),public.synthetic_calendar_source_visible() FROM PUBLIC;
      GRANT EXECUTE ON FUNCTION public.synthetic_calendar_read_finished(),public.synthetic_calendar_source_visible() TO openetrm_security_owner;""")
    try:
        assert pins.count('\nEND;\n$function$') == 1
        query(pins.replace('\nEND;\n$function$', '\nPERFORM public.synthetic_calendar_read_finished();\nEND;\n$function$', 1))
        yield
    finally:
        query(pins)
        query('DROP FUNCTION public.synthetic_calendar_read_finished(),public.synthetic_calendar_source_visible();DROP TABLE public.synthetic_calendar_read_context;')


def mixed_source_checks(client, query, manifest, source, publication_check=True):
    """Independent evaluate oracle for the same exact mixed close under a metadata fault."""
    session = 'synthetic_calendar_mixed'
    publish_session(query, manifest, session, 'Synthetic mixed sources', [
        {'curves': ['ng_monthly'], 'cutoff': '16:00', 'from': '2020-01-01', 'to': '2100-01-01'}])
    secondary = manifest['inputMarkets'][0]
    revision = secondary['revisionId']
    query(f"INSERT INTO market_data.close_session_curves VALUES('{manifest['catalogId']}','{session}',1,'{revision}','daily_gas');")
    selected = deepcopy(next(f for f in secondary['frames'] if f['at'] == source['at']))
    selected['curveContracts'] = {'daily_gas': json.loads(query(f"SELECT json_agg(contract_id ORDER BY ordinal) FROM market_data.curve_members WHERE(catalog_id,revision_id,curve_code)=('{manifest['catalogId']}','{revision}','daily_gas');"))}
    close = record_close(query, manifest, session, 1, source, retained=[(revision, selected)])
    request = {'session': session, 'curve': 'ng_monthly', 'timeZone': 'UTC'}
    assert client.call('market/dates', request)['dates'][0]['frames'][0]['id'] == close
    client.call('profile', {'profile': 'approver'})
    prior = client.call('catalogue')
    original = {key: prior[key] for key in ('effectiveFrom', 'effectiveTo', 'curves', 'roles')}
    changed = deepcopy(original)
    next(curve for curve in changed['curves'] if curve['code'] == 'interest_rates')['definitionRevisionId'] = revision
    published = client.call('catalogue/publish', {'requestId': str(uuid4()), 'expectedHead': prior['id'], 'definition': changed})
    restored = False
    failures = []
    try:
        query(f"CREATE POLICY synthetic_calendar_missing_terms ON market_data.contracts AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(revision_id<>'{revision}'::uuid);")
        try:
            # Typed rate membership remains readable; its unrelated commodity
            # terms must not become a condition for this primary commodity.
            assert client.call('evaluate', {'snapshotId': close, 'curve': 'ng_monthly'})['points']
            if not client.call('market/dates', request)['dates']:
                failures.append('An unrequested rate owner materialized unrelated missing commodity terms')
            client.call('catalogue/publish', {'requestId': str(uuid4()), 'expectedHead': published['id'], 'definition': original})
            restored = True
            # The selected supplemental commodity does require those terms.
            # Its unchanged evaluation read supplies the exact refusal oracle.
            refusal = client.call('evaluate', {'snapshotId': close, 'curve': 'daily_gas'}, expected=422)
            try:
                answer = client.call('market/dates', {**request, 'curve': 'daily_gas'})
                assert answer['dates'] == [] and answer['unavailable'] == [{'date': '2020-12-18', 'reason': refusal['code']}], answer
            except AssertionError as error:
                failures.append('Supplemental metadata failure escaped its date: '+str(error))
        finally:
            query('DROP POLICY synthetic_calendar_missing_terms ON market_data.contracts;')
    finally:
        if not restored:client.call('catalogue/publish', {'requestId': str(uuid4()), 'expectedHead': published['id'], 'definition': original})
        client.call('profile', {'profile': 'viewer'})
    assert not failures, '; '.join(failures)
    print('PASS mixed-source calendar metadata faults match independent exact evaluation and do not materialize unrequested rate-owner terms', flush=True)
    if not publication_check:return
    # End authority for the unconsumed part only in the NEXT identity
    # transaction. The immutable matrix was valid throughout execution; final
    # header RLS must still refuse disclosure without another matrix payload.
    fact_signature = 'security.market_fact_visible(uuid,uuid,uuid)'
    fact = query(f"SELECT pg_get_functiondef('{fact_signature}'::regprocedure);")
    with original_read_probe(query):
        try:
            assert fact.count('\nBEGIN\n') == 1
            query(fact.replace('\nBEGIN\n', f"\nBEGIN\n IF revision='{revision}'::uuid AND NOT public.synthetic_calendar_source_visible() THEN RETURN false;END IF;\n", 1))
            client.call('market/dates', request, expected=403)
        finally:
            query(fact)
    print('PASS calendar disclosure rechecks unconsumed supplemental source authority through native close RLS', flush=True)


def limited_matrix_check(client, query, older):
    """Discovery attests all frames; a successful newest read attests only its candidate again."""
    signature = 'security.read_market_close_parts(uuid,uuid[],uuid)'
    original = query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    query('CREATE SEQUENCE public.synthetic_calendar_matrix_reads;GRANT USAGE ON SEQUENCE public.synthetic_calendar_matrix_reads TO openetrm_security_owner;')
    try:
        assert original.count('\nBEGIN\n') == 1
        query(original.replace('\nBEGIN\n', f"\nBEGIN\n IF nextval('public.synthetic_calendar_matrix_reads')>1 AND '{older}'::uuid=ANY(close_ids) THEN RAISE EXCEPTION 'Synthetic unvisited older matrix read' USING ERRCODE='42501';END IF;\n", 1))
        for maximum in (1, 2):
            query('ALTER SEQUENCE public.synthetic_calendar_matrix_reads RESTART;')
            client.call('market/dates', {'session': SESSION, 'curve': 'bridge_basis', 'timeZone': 'UTC', 'maximumDates': maximum})
        query('ALTER SEQUENCE public.synthetic_calendar_matrix_reads RESTART;')
        client.call('market/last-closes', {'session': SESSION})
    finally:
        query(original);query('DROP SEQUENCE public.synthetic_calendar_matrix_reads;')
    print('PASS latest calendar and last closes do not reattest unvisited older matrices after discovery', flush=True)


def discovery_work_check(client, query, request):
    """Warm readers reauthorize headers without rebuilding unchanged discovery payloads."""
    signature = 'security.read_market_close_parts(uuid,uuid[],uuid)'
    native = query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    query('CREATE SEQUENCE public.synthetic_calendar_discovery_reads;GRANT USAGE ON SEQUENCE public.synthetic_calendar_discovery_reads TO openetrm_security_owner;')
    counts = {}
    try:
        assert native.count('\nBEGIN\n') == 1
        query(native.replace('\nBEGIN\n', "\nBEGIN\n PERFORM nextval('public.synthetic_calendar_discovery_reads');\n", 1))
        for name, path, body in [('full', 'market/dates', request), ('latest', 'market/dates', {**request, 'maximumDates': 1}),
                                  ('last', 'market/last-closes', {'session': request['session']}), ('navigation', 'workspace/navigation', None)]:
            query('ALTER SEQUENCE public.synthetic_calendar_discovery_reads RESTART;')
            client.call(path, body)
            counts[name] = int(query('SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM public.synthetic_calendar_discovery_reads;'))
    finally:
        query(native);query('DROP SEQUENCE public.synthetic_calendar_discovery_reads;')
    print('CALENDAR_MATRIX_READS=' + json.dumps(counts), flush=True)
    assert counts == {'full': 1, 'latest': 1, 'last': 1, 'navigation': 0}, counts
    print('PASS warm discovery does not rematerialize immutable matrices; calendar execution still reattests its exact inputs', flush=True)


def native_source_work_check(client, query, request):
    """One-source matrix admission must not repeat source authority for every close."""
    signatures = ['security.read_market_close_parts(uuid,uuid[],uuid)', 'security.market_fact_visible(uuid,uuid,uuid)']
    parts, fact = [query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);") for signature in signatures]
    scalar = query("SELECT pg_get_functiondef('security.market_close_visible(uuid,uuid)'::regprocedure);")
    query('CREATE SEQUENCE public.synthetic_calendar_source_checks;GRANT USAGE ON SEQUENCE public.synthetic_calendar_source_checks TO openetrm_security_owner;')
    try:
        assert parts.count('\nBEGIN\n') == 1 and parts.count('RETURN payload;') == 1 and fact.count('\nBEGIN\n') == 1
        query(parts.replace('\nBEGIN\n', "\nBEGIN\n PERFORM set_config('openetrm.synthetic_calendar_batch','on',true);\n", 1)
              .replace('RETURN payload;', "PERFORM set_config('openetrm.synthetic_calendar_batch','off',true);RETURN payload;"))
        query(fact.replace('\nBEGIN\n', "\nBEGIN\n IF current_setting('openetrm.synthetic_calendar_batch',true)='on' THEN PERFORM nextval('public.synthetic_calendar_source_checks');END IF;\n", 1))
        answer = projection(client.call('market/dates', request))
        calls = int(query('SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM public.synthetic_calendar_source_checks;'))
        # A semantically identical but independently changed scalar must remain
        # authoritative, rather than being bypassed by copied admission logic.
        query(scalar.replace('\nBEGIN\n', '\nBEGIN\n -- Synthetic scalar compatibility probe\n', 1))
        query('ALTER SEQUENCE public.synthetic_calendar_source_checks RESTART;')
        assert projection(client.call('market/dates', request)) == answer
        fallback = int(query('SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM public.synthetic_calendar_source_checks;'))
    finally:
        query(parts);query(fact);query(scalar);query('DROP SEQUENCE public.synthetic_calendar_source_checks;')
    print('CALENDAR_MATRIX_SOURCE_CHECKS=' + str(calls), flush=True)
    assert 0 < calls <= 4, f'One exact source was authorized {calls} times in one matrix batch'
    assert fallback > calls, f'Scalar drift did not retain the authoritative original predicate: {calls}/{fallback}'
    assert query("SELECT has_function_privilege('preview_runtime','security.market_close_batch_visible(uuid,uuid[])','EXECUTE');") == 'f'
    print('PASS native matrix admission shares exact-source checks within its statement', flush=True)


def native_hidden_work_check(client, query, request):
    """A fully visible session needs source admission, not one admission per day."""
    signature = 'security.market_fact_visible(uuid,uuid,uuid)'
    fact = query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    hidden = query("SELECT pg_get_functiondef('security.close_session_hidden_days(uuid,text)'::regprocedure);")
    query('CREATE SEQUENCE public.synthetic_calendar_hidden_checks;GRANT USAGE ON SEQUENCE public.synthetic_calendar_hidden_checks TO openetrm_security_owner;')
    query('CREATE SEQUENCE public.synthetic_calendar_hidden_calls;GRANT USAGE ON SEQUENCE public.synthetic_calendar_hidden_calls TO openetrm_security_owner;')
    try:
        assert fact.count('\nBEGIN\n') == 1
        query(fact.replace('\nBEGIN\n', "\nDECLARE stack text;\nBEGIN\n GET DIAGNOSTICS stack=PG_CONTEXT;\n IF position('close_session_hidden_days(' IN stack)>0 THEN PERFORM nextval('public.synthetic_calendar_hidden_checks');END IF;\n", 1))
        query(hidden.replace('\nBEGIN\n', "\nBEGIN\n PERFORM nextval('public.synthetic_calendar_hidden_calls');\n", 1))
        client.call('market/dates', {**request, 'maximumDates': 1})
        calls = int(query('SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM public.synthetic_calendar_hidden_checks;'))
        invocations = int(query('SELECT CASE WHEN is_called THEN last_value ELSE 0 END FROM public.synthetic_calendar_hidden_calls;'))
    finally:
        query(fact);query(hidden);query('DROP SEQUENCE public.synthetic_calendar_hidden_checks,public.synthetic_calendar_hidden_calls;')
    print('CALENDAR_HIDDEN_FUNCTION_CALLS=' + str(invocations), flush=True)
    print('CALENDAR_HIDDEN_SOURCE_CHECKS=' + str(calls), flush=True)
    assert invocations == 2, f'Two hidden-day reads evaluated their native function {invocations} times'
    assert 0 < calls <= 4, f'One exact source was authorized {calls} times while proving an entirely visible session'
    print('PASS fully visible hidden-day proofs share their source admission', flush=True)


def hidden_boundaries_check(client, query, manifest, request):
    """Hidden metadata remains exact for empty, wide, replaced and null projections."""
    catalog, tenant, workspace = (manifest[key] for key in ('catalogId', 'tenantId', 'workspaceId'))
    token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','viewer','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
    def read(session):
        return json.loads(query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');SELECT array_to_json(security.close_session_hidden_days('{catalog}','{session}'));ROLLBACK;").splitlines()[-1])
    assert read('synthetic_empty_calendar') == []
    session = 'synthetic_wide_hidden_calendar'
    publish_session(query, manifest, session, 'Synthetic wide calendar', [
        {'curves': ['ng_monthly'], 'cutoff': '16:00', 'from': '2020-01-01', 'to': '2100-01-01'}])
    query(f"INSERT INTO presentation.close_session_listings VALUES('{catalog}','{session}',1,1,'Synthetic wide calendar',false,pg_current_xact_id());")
    source = next(frame for frame in manifest['frames'] if frame['at'].startswith('2020-12-11T16:00:00'))
    originals = []
    for index in range(129):
        frame = deepcopy(source)
        frame['at'] = (date(2020, 1, 1)+timedelta(days=index)).isoformat()+'T16:00:00Z'
        frame['curveContracts'] = {'ng_monthly': frame['curveContracts']['ng_monthly']}
        originals.append(record_close(query, manifest, session, 1, frame))
    assert read(session) == []
    frame['at'] = '2020-01-01T16:00:00Z'
    head = record_close(query, manifest, session, 1, frame)
    scalar = query("SELECT pg_get_functiondef('security.market_close_visible(uuid,uuid)'::regprocedure);")
    try:
        query(scalar.replace('\nBEGIN\n', f"\nBEGIN\n IF close_id='{head}'::uuid THEN RETURN false;END IF;\n", 1))
        assert read(session) == ['2020-01-01'], 'A visible predecessor replaced its hidden known head'
    finally:
        query(scalar)
    selected = ','.join("'"+identity+"'::uuid" for identity in originals)
    query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');DO $$BEGIN BEGIN PERFORM security.read_market_close_parts('{catalog}',ARRAY[{selected}],NULL);RAISE EXCEPTION 'synthetic_public_close_limit_removed';EXCEPTION WHEN insufficient_privilege THEN NULL;END;END$$;ROLLBACK;")
    before = projection(client.call('market/dates', request))
    hidden = query("SELECT pg_get_functiondef('security.close_session_hidden_days(uuid,text)'::regprocedure);")
    prefix, separator, _ = hidden.partition('AS $function$')
    assert separator
    try:
        query(prefix+separator+'BEGIN RETURN NULL;END;$function$;')
        assert projection(client.call('market/dates', request)) == before
    finally:
        query(hidden)
    print('PASS hidden-day projection preserves empty, more-than-128-day, hidden-head, public-limit and legacy-null behavior', flush=True)


def batch_authority_check(query, manifest):
    """A prepublished successor grant cannot extend a native batch's initial authority."""
    catalog, tenant, workspace = (manifest[key] for key in ('catalogId', 'tenantId', 'workspaceId'))
    principal = "(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='viewer')"
    closes = query(f"SELECT string_agg(quote_literal(id)||'::uuid',',') FROM(SELECT id FROM market_data.market_closes WHERE session_code='demo_close_1600' ORDER BY business_date DESC LIMIT 2)c;")
    selected = [frame for frame in manifest['frames'] if frame['at'] in ('2020-12-17T16:00:00Z', '2020-12-18T16:00:00Z')]
    assert len(selected) == 2
    pins = json.dumps([{'id': str(uuid4()), 'pins': frame['observationIds']} for frame in selected]).replace("'", "''")
    pin_call = f"security.read_observation_pin_sets('{catalog}','{manifest['revisionId']}','{pins}'::jsonb)"
    failures = []
    for name, signature, call, fallback in [
        ('catalogue', 'security.read_request_catalogue(uuid,date)', f"security.read_request_catalogues('{catalog}',ARRAY[DATE '2020-12-18'])", ''),
        ('matrix fast', 'security.read_market_close_parts(uuid,uuid[],uuid)', f"security.read_market_close_parts_results('{catalog}',ARRAY[{closes}],NULL)", ''),
        ('matrix fallback', 'security.read_market_close_parts(uuid,uuid[],uuid)', f"security.read_market_close_parts_results('{catalog}',ARRAY[{closes}],NULL)", 'cardinality(close_ids)>1'),
        ('pins fast', 'security.read_observation_pins(uuid,uuid,uuid[])', pin_call, ''),
        ('pins fallback', 'security.read_observation_pins(uuid,uuid,uuid[])', pin_call, f"cardinality(requested_pins)>{max(len(frame['observationIds']) for frame in selected)}"),
        ('hidden fast', 'security.close_in_force(uuid,text,date)', f"security.close_session_hidden_days('{catalog}','demo_close_1600')", ''),
        ('hidden fallback', 'security.close_in_force(uuid,text,date)', f"security.close_session_hidden_days('{catalog}','demo_close_1600')", ''),
    ]:
        native = query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
        assert native.count('\nBEGIN\n') == 1
        delay = (f" IF {fallback} THEN RAISE EXCEPTION 'Synthetic batch fallback' USING ERRCODE='42501';END IF;\n" if fallback else '') + ' PERFORM pg_sleep(2.3);\n'
        extra = ''
        if name.startswith('hidden'):
            extra = 'CREATE SEQUENCE public.synthetic_calendar_grant_pause;GRANT USAGE ON SEQUENCE public.synthetic_calendar_grant_pause TO openetrm_security_owner;'
            delay = " IF nextval('public.synthetic_calendar_grant_pause')=1 THEN PERFORM pg_sleep(2.3);END IF;\n"
            if name.endswith('fallback'):
                scalar = query("SELECT pg_get_functiondef('security.market_close_visible(uuid,uuid)'::regprocedure);")
                prefix, separator, _ = scalar.partition('AS $function$')
                assert separator
                extra += prefix+separator+'BEGIN RETURN false;END;$function$;'
        patched = native.replace('\nBEGIN\n', '\nBEGIN\n'+delay, 1)
        # One owner transaction rolls back both the grants and native fault.
        # No concurrent grant write or security-generation change masks expiry.
        actual = query('BEGIN;'+extra+patched+';'
            + f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '2 seconds' WHERE principal_id={principal} AND NOT revoked;"
            + f"INSERT INTO security.role_grants SELECT gen_random_uuid(),principal_id,role_id,tenant_id,workspace_id,granted_to-interval '1 second','9999-12-31',false FROM security.role_grants WHERE principal_id={principal} AND NOT revoked;"
            + 'CREATE TEMP TABLE synthetic_calendar_token(token uuid);GRANT SELECT,INSERT ON synthetic_calendar_token TO preview_authenticator,preview_runtime;'
            + 'SET SESSION AUTHORIZATION preview_authenticator;'
            + f"INSERT INTO synthetic_calendar_token SELECT security.issue_context('https://preview.invalid','viewer','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);"
            + 'SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context((SELECT token FROM synthetic_calendar_token));'
            + f"DO $$DECLARE denied boolean=false;until_at timestamptz=security.authority_until('ReadMarketData');BEGIN "
            + "IF until_at IS NULL OR clock_timestamp()>=until_at OR until_at>clock_timestamp()+interval '3 seconds' THEN RAISE EXCEPTION 'synthetic_initial_grant';END IF;"
            + f"BEGIN PERFORM * FROM {call};EXCEPTION WHEN insufficient_privilege THEN denied=true;END;"
            + f"IF clock_timestamp()<until_at OR security.market_access('{catalog}','ReadMarketData') IS NOT TRUE THEN RAISE EXCEPTION 'synthetic_successor_not_live';END IF;"
            + "PERFORM set_config('openetrm.synthetic_calendar_denied',denied::text,true);END$$;"
            + "SELECT current_setting('openetrm.synthetic_calendar_denied');ROLLBACK;").splitlines()[-1]
        if actual != 'true':failures.append(name+' extended its initial authority')
    assert not failures, '; '.join(failures)
    print('PASS catalogue, matrix and pin batches retain their original authority despite a live later-starting grant', flush=True)


def failed_build_check(client, query, manifest, measure):
    """Selected roots with independently invalid future pins must not restore a per-close SQL loop."""
    session = 'synthetic_calendar_failed_builds'
    def listing(code, listed):
        query(f"""INSERT INTO presentation.close_session_listings
          SELECT s.catalog_id,s.code,coalesce((SELECT max(l.version) FROM presentation.close_session_listings l WHERE(l.catalog_id,l.code)=(s.catalog_id,s.code)),0)+1,
           max(s.version),'Synthetic calendar workload',{str(listed).lower()},pg_current_xact_id()
          FROM market_data.close_session_versions s WHERE s.code='{code}' GROUP BY s.catalog_id,s.code;""")
    listing(SESSION, False)
    publish_session(query, manifest, session, 'Synthetic failed qualification', [
        {'curves': ['ng_monthly'], 'cutoff': '16:00', 'from': '2020-01-01', 'to': '2100-01-01'}])
    counts, runs = {}, []
    try:
        for day in ('2020-12-17', '2020-12-18'):
            record_close(query, manifest, session, 1, next(f for f in manifest['frames'] if f['at'].startswith(day+'T16:00:00')))
        original = next(f for f in manifest['frames'] if f['at'].startswith('2020-12-11T16:00:00'))
        for total in (24, 48, 96):
            for index in range(max(counts, default=2)-2, total-2):
                frame = deepcopy(original)
                frame['at'] = (date(2020, 10, 1)-timedelta(days=index)).isoformat()+'T16:00:00Z'
                frame['curveContracts'] = {'ng_monthly': frame['curveContracts']['ng_monthly']}
                # Distinct pin sets prevent an identical-input memo from
                # satisfying the scaling check instead of batching the read.
                frame['observationIds'] = [pin for bit, pin in enumerate(original['observationIds'])
                                           if bit >= 7 or not index & (1 << bit)]
                # Deliberately invalid retained inputs: the observations follow
                # the close. The managed cutoff rule must refuse every one.
                record_close(query, manifest, session, 1, frame)
            answer, sample = measure('market/dates', {'session': session, 'curve': 'ng_monthly', 'timeZone': 'UTC', 'maximumDates': 10})
            assert len(answer['dates']) == 2 and len(answer['unavailable']) == total-2, answer
            assert {day['reason'] for day in answer['unavailable']} == {'market_data_unavailable'}
            counts[total] = sample['statements'];runs.append({'closes': total, **sample})
        print('CALENDAR_FAILED_BUILD_MEASUREMENTS=' + json.dumps(runs), flush=True)
        # A third genuine close lets a missing pin in the second close prove
        # that other frames in the same future batch remain independently usable.
        first = next(f for f in manifest['frames'] if f['at'].startswith('2020-12-18T16:00:00'))
        second = next(f for f in manifest['frames'] if f['at'].startswith('2020-12-17T16:00:00'))
        third = next(f for f in manifest['frames'] if f['at'].startswith('2020-12-16T16:00:00'))
        record_close(query, manifest, session, 1, third)
        old_pin = next(pin for pin in original['observationIds'] if pin not in first['observationIds'] and pin not in second['observationIds'] and pin not in third['observationIds'])
        signature = 'security.read_observation_pins(uuid,uuid,uuid[])'
        native = query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
        request = {'session': session, 'curve': 'ng_monthly', 'timeZone': 'UTC'}
        try:
            assert native.count('\nBEGIN\n') == 1
            query(native.replace('\nBEGIN\n', f"\nBEGIN\n IF '{old_pin}'::uuid=ANY(requested_pins) THEN RAISE EXCEPTION 'Synthetic unvisited older pin denial' USING ERRCODE='42501';END IF;\n", 1))
            assert len(client.call('market/dates', {**request, 'maximumDates': 2})['dates']) == 2
            client.call('market/dates', {**request, 'maximumDates': 4}, expected=403)
        finally:
            query(native)
        missing = next(pin for pin in second['observationIds'] if pin not in first['observationIds'] and pin not in third['observationIds'] and pin not in original['observationIds'])
        with visibility_probe(query, f"v.observation<>'{missing}'::uuid"):
            actual = client.call('market/dates', {**request, 'maximumDates': 2})
            assert [day['date'] for day in actual['dates']] == ['2020-12-16', '2020-12-18'], actual
            assert actual['unavailable'] == [{'date': '2020-12-17', 'reason': 'market_data_unavailable'}]
        # The successful union also contains a third good close. Its facts
        # are prefetched, but maximumDates:2 does not consume their authority.
        unvisited = next(pin for pin in third['observationIds'] if pin not in first['observationIds'] and pin not in second['observationIds'])
        with original_read_probe(query), visibility_probe(query, f"v.observation<>'{unvisited}'::uuid OR public.synthetic_calendar_source_visible()"):
            actual = client.call('market/dates', {**request, 'maximumDates': 2})
            assert [day['date'] for day in actual['dates']] == ['2020-12-17', '2020-12-18'], actual
        print('PASS limited pin reads defer unvisited denials, refuse reached denials and isolate missing pins to their own frame', flush=True)
        require_constant_statements(counts)
    finally:
        listing(session, False);listing(SESSION, True)
    print('PASS selected roots that fail cutoff qualification keep constant limited-calendar statement counts', flush=True)


def profile_native(client, query, request):
    """Measure completed child-function deltas around each native invocation."""
    signatures = ['security.read_market_close_parts(uuid,uuid[],uuid)', 'security.close_session_hidden_days(uuid,text)']
    originals = [query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);") for signature in signatures]
    query("""CREATE TABLE public.synthetic_calendar_function_work(ordinal bigint GENERATED ALWAYS AS IDENTITY,transaction_id xid8,label text,work jsonb);
      CREATE FUNCTION public.synthetic_calendar_work() RETURNS jsonb LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
       SELECT coalesce(jsonb_object_agg(funcid::regprocedure::text,jsonb_build_object('calls',calls,'selfMs',self_time,'totalMs',total_time)),'{}')
       FROM pg_stat_xact_user_functions;
      $$;
      CREATE FUNCTION public.synthetic_calendar_capture_work(label text,prior jsonb) RETURNS void LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
       INSERT INTO public.synthetic_calendar_function_work(transaction_id,label,work)
       SELECT pg_current_xact_id(),label,jsonb_object_agg(signature,jsonb_build_object(
        'calls',calls-coalesce((prior->signature->>'calls')::bigint,0),
        'selfMs',self_time-coalesce((prior->signature->>'selfMs')::double precision,0),
        'totalMs',total_time-coalesce((prior->signature->>'totalMs')::double precision,0)))
       FROM(SELECT *,funcid::regprocedure::text signature FROM pg_stat_xact_user_functions)s;
      $$;
      REVOKE ALL ON FUNCTION public.synthetic_calendar_work(),public.synthetic_calendar_capture_work(text,jsonb) FROM PUBLIC;
      GRANT EXECUTE ON FUNCTION public.synthetic_calendar_work(),public.synthetic_calendar_capture_work(text,jsonb) TO openetrm_security_owner;""")
    try:
        parts, hidden = originals
        assert parts.count('RETURN payload;') == 1
        query(parts.replace('\nDECLARE ', '\nDECLARE work_before jsonb;', 1)
              .replace('\nBEGIN\n', '\nBEGIN\nwork_before=public.synthetic_calendar_work();\n', 1)
              .replace('RETURN payload;', "PERFORM public.synthetic_calendar_capture_work('matrix',work_before);RETURN payload;"))
        assert hidden.count('\nBEGIN\n') == 1 and hidden.count('RETURN coalesce(') == 1
        query(hidden.replace(' STABLE ', ' VOLATILE ').replace('\nBEGIN\n', '\nDECLARE payload date[];work_before jsonb;\nBEGIN\nwork_before=public.synthetic_calendar_work();\n', 1)
              .replace('RETURN coalesce(', 'payload=coalesce(', 1)
              .replace('\nEND;', "\nPERFORM public.synthetic_calendar_capture_work('hidden',work_before);RETURN payload;\nEND;", 1))
        for name, path, body in [('full', 'market/dates', request), ('latest', 'market/dates', {**request, 'maximumDates': 1}),
                                  ('last', 'market/last-closes', {'session': request['session']}), ('navigation', 'workspace/navigation', None)]:
            query('TRUNCATE public.synthetic_calendar_function_work;')
            client.call(path, body)
            work = json.loads(query("SELECT coalesce(json_agg(json_build_object('stage',label,'work',work) ORDER BY ordinal),'[]') FROM public.synthetic_calendar_function_work;"))
            print('CALENDAR_NATIVE_WORK=' + json.dumps({'request': name, 'transactions': work}), flush=True)
    finally:
        for original in originals:query(original)
        query('DROP FUNCTION public.synthetic_calendar_work(),public.synthetic_calendar_capture_work(text,jsonb);DROP TABLE public.synthetic_calendar_function_work;')


def native_missing_close_check(query, manifest):
    """Scalar drift cannot remove the reader's independent missing-ID refusal."""
    scalar = query("SELECT pg_get_functiondef('security.market_close_visible(uuid,uuid)'::regprocedure);")
    catalog, tenant, workspace = (manifest[key] for key in ('catalogId', 'tenantId', 'workspaceId'))
    token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','viewer','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
    prefix, separator, _ = scalar.partition('AS $function$')
    assert separator
    try:
        query(prefix+separator+'BEGIN RETURN true;END;$function$;')
        query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');"
              + f"DO $$BEGIN BEGIN PERFORM security.read_market_close_parts('{catalog}',ARRAY['{uuid4()}'::uuid],NULL);"
              + "RAISE EXCEPTION 'synthetic_missing_close_accepted';EXCEPTION WHEN insufficient_privilege THEN NULL;END;END$$;ROLLBACK;")
    finally:
        query(scalar)
    print('PASS native matrix reader refuses missing IDs even when a changed scalar accepts them', flush=True)


def native_admission_parity(query, manifest):
    """Compare private set admission with the independently retained scalar oracle."""
    signature = 'security.market_close_batch_visible(uuid,uuid[])'
    if query(f"SELECT to_regprocedure('{signature}') IS NULL;") == 't':return
    catalog, tenant, workspace = (manifest[key] for key in ('catalogId', 'tenantId', 'workspaceId'))
    closes = query(f"SELECT string_agg(quote_literal(id)||'::uuid',',') FROM market_data.market_closes WHERE catalog_id='{catalog}';")
    query("""CREATE FUNCTION public.synthetic_close_batch_compare(catalog uuid,ids uuid[]) RETURNS jsonb
      LANGUAGE sql VOLATILE SECURITY DEFINER SET search_path=pg_catalog AS $$
       WITH eligible AS MATERIALIZED(SELECT ARRAY(SELECT id FROM unnest($2)id WHERE security.market_close_visible($1,id)) selected_ids)
       SELECT jsonb_build_object('actual',security.market_close_batch_visible($1,$2),
        'expected',NOT EXISTS(SELECT 1 FROM unnest($2)i WHERE security.market_close_visible($1,i) IS NOT TRUE),
        'eligibleCount',cardinality(eligible.selected_ids),'eligibleActual',CASE WHEN cardinality(eligible.selected_ids)>0
         THEN security.market_close_batch_visible($1,eligible.selected_ids) ELSE false END) FROM eligible;
      $$;
      ALTER FUNCTION public.synthetic_close_batch_compare(uuid,uuid[]) OWNER TO openetrm_security_owner;
      REVOKE ALL ON FUNCTION public.synthetic_close_batch_compare(uuid,uuid[]) FROM PUBLIC;
      GRANT EXECUTE ON FUNCTION public.synthetic_close_batch_compare(uuid,uuid[]) TO openetrm_runtime;""")
    try:
        for day in (None, '2020-12-18', '2020-12-17'):
            token = query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','viewer','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
            bind = '' if day is None else f"SELECT security.use_market_context('{catalog}',security.capture_date_context('{catalog}','{day}',clock_timestamp()));"
            actual = json.loads(query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{bind}SELECT public.synthetic_close_batch_compare('{catalog}',ARRAY[{closes}]);ROLLBACK;").splitlines()[-1])
            print('CALENDAR_NATIVE_PARITY=' + json.dumps({'day': day, **actual}), flush=True)
            assert actual['actual'] == actual['expected'], actual
            assert actual['eligibleActual'] == (actual['eligibleCount']>0), actual
    finally:
        query('DROP FUNCTION public.synthetic_close_batch_compare(uuid,uuid[]);')


def run_checks(url, query, manifest, psql, environment, log, api_pid, *, verify=True, profile=False):
    client = PreviewClient(url); client.bootstrap(); client.call('profile', {'profile': 'viewer'})
    if verify:native_admission_parity(query, manifest)
    if verify:native_missing_close_check(query, manifest)
    body = {'session': 'demo_close_1600', 'curve': 'bridge_basis', 'timeZone': 'UTC'}
    answers = {}
    for name, curve in [('curve', 'bridge_basis'), ('workspace', None), ('rate', 'interest_rates')]:
        answers[name] = projection(client.call('market/dates', {**body, 'curve': curve}))
    client.call('market/context', {'effectiveOn': '2020-12-18', 'knownAt': None, 'mode': 'current', 'selection': 'latest_correction'})
    answers['retained'] = projection(client.call('market/dates', body))
    client.call('market/current', {})
    source = next(f for f in manifest['frames'] if f['at'].startswith('2020-12-18T16:00:00'))
    replaced = record_close(query, manifest, 'demo_close_1600',
                            int(query(f"SELECT session_version FROM market_data.market_closes WHERE id='{source['closeId']}';")), source)
    answers['replaced'] = projection(client.call('market/dates', body))
    signature = 'security.market_close_visible(uuid,uuid)'
    original = query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    try:
        assert original.count('\nBEGIN\n') == 1
        query(original.replace('\nBEGIN\n', f"\nBEGIN\n IF close_id='{replaced}'::uuid THEN RETURN false;END IF;\n", 1))
        answers['hidden'] = projection(client.call('market/dates', body))
    finally:
        query(original)
    client.call('profile', {'profile': 'admin'})
    answers['denied'] = client.call('market/dates', body, expected=403)
    client.call('profile', {'profile': 'viewer'})
    actual = {name: hashlib.sha256(json.dumps(answer, sort_keys=True).encode()).hexdigest() for name, answer in answers.items()}
    assert actual == EXPECTED, (actual, answers)
    print('PASS calendar answers equal the unchanged implementation for curve, workspace, rate, retained, replaced, hidden and denied reads', flush=True)
    signature = 'security.read_request_catalogue(uuid,date)'
    original = query(f"SELECT pg_get_functiondef('{signature}'::regprocedure);")
    try:
        assert original.count('\nBEGIN\n') == 1
        query(original.replace('\nBEGIN\n', "\nBEGIN\n IF effective_on IN(DATE '2020-12-16',DATE '2020-12-18') THEN RAISE EXCEPTION 'catalogue_missing' USING ERRCODE='P0001';END IF;\n", 1))
        # The unchanged calendar catches the translated SQL error per date,
        # then fails on its aborted transaction. Last-closes propagates the
        # structured error immediately. Batching must preserve both boundaries.
        assert client.call('market/dates', body, expected=500)['code'] == 'preview_error'
        assert client.call('market/last-closes', {'session': body['session']}, expected=422)['code'] == 'catalogue_missing'
    finally:
        query(original)
    try:
        query(original.replace('\nBEGIN\n', "\nBEGIN\n IF effective_on=DATE '2020-11-18' THEN RAISE EXCEPTION 'Synthetic inaccessible older catalogue' USING ERRCODE='42501';END IF;\n", 1))
        for maximum in (1, 2):
            limited = projection(client.call('market/dates', {**body, 'maximumDates': maximum}))
            assert limited['dates'] == answers['replaced']['dates'][-maximum:]
        client.call('market/dates', body, expected=403)
    finally:
        query(original)
    mixed_source_checks(client, query, manifest, source, publication_check=verify)
    if verify:batch_authority_check(query, manifest)
    # Keep 96 candidates within the global 128-frame discovery bound. Existing facts stay immutable.
    query("INSERT INTO presentation.close_session_listings SELECT v.catalog_id,v.code,1,max(v.version),'Synthetic retired fixture session',false,pg_current_xact_id() FROM market_data.close_session_versions v GROUP BY v.catalog_id,v.code;")

    # Two complete bridge closes, then increasingly many closes which only declare ng_monthly.
    # Session versions make root membership explicit; no inferred roots or financial inputs.
    publish_session(query, manifest, SESSION, 'Synthetic calendar scaling', [
        {'curves': ['ng_monthly'], 'cutoff': '16:00', 'from': '2020-01-01', 'to': '2020-12-17'},
        {'curves': sorted(source['curveContracts']), 'cutoff': '16:00', 'from': '2020-12-17', 'to': '2100-01-01'}])
    for day in ('2020-12-17', '2020-12-18'):
        record_close(query, manifest, SESSION, 2, next(f for f in manifest['frames'] if f['at'].startswith(day+'T16:00:00')))
    older = next(f for f in manifest['frames'] if f['at'].startswith('2020-12-11T16:00:00'))
    counts, report = {}, []
    def measure(path, request):
        offset = log.stat().st_size
        def cpu():
            fields = Path(f'/proc/{api_pid}/stat').read_text().rsplit(')', 1)[1].split()
            return (int(fields[11])+int(fields[12])) / os.sysconf('SC_CLK_TCK')
        cpu_start = cpu()
        start = time.perf_counter(); answer = client.call(path, request); elapsed = time.perf_counter()-start
        with log.open() as stream:
            stream.seek(offset); lines = stream.read().splitlines()
        statements = [line for line in lines if re.match(r'preview_runtime \d+ LOG:  duration: .* (?:execute [^:]*|statement):', line)]
        db_ms = sum(float(re.search(r'duration: ([\d.]+) ms', line)[1]) for line in statements)
        categories = {}
        for line in statements:
            # Labels use the logged statement's first line. Multiline SQL whose
            # native call is on a continuation line remains in "other".
            category = next((name for name in ('read_request_catalogues', 'read_market_close_parts_results', 'read_market_close_parts', 'read_observation_pin_sets', 'read_observation_pins', 'close_in_force', 'close_session_hidden_days') if name+'(' in line), 'other')
            categories[category] = round(categories.get(category, 0) + float(re.search(r'duration: ([\d.]+) ms', line)[1]), 3)
        return answer, {'seconds': round(elapsed, 6), 'statements': len(statements), 'databaseMs': round(db_ms, 3),
                        'apiCpuMs': round((cpu()-cpu_start)*1000, 3), 'databaseBreakdownMs': categories}
    for total in (24, 48, 96):
        for index in range(len(counts) and max(counts)-2 or 0, total-2):
            frame = deepcopy(older)
            frame['at'] = (date(2020, 12, 16)-timedelta(days=index)).isoformat()+'T16:00:00Z'
            frame['curveContracts'] = {'ng_monthly': frame['curveContracts']['ng_monthly']}
            record_close(query, manifest, SESSION, 1, frame)
        request = {**body, 'session': SESSION}
        client.call('market/dates', request)  # warm JIT/pool
        samples = [measure('market/dates', request) for _ in range(3)]
        assert all(len(answer['dates']) == 2 and len(answer['unavailable']) == total-2 for answer, _ in samples), samples[0]
        counts[total] = max(sample['statements'] for _, sample in samples)
        report.append({'closes': total, 'request': 'curve', 'samples': [sample for _, sample in samples]})
        if total == 96:
            for name, path, selected in [('latest', 'market/dates', {**request, 'maximumDates': 1}),
                                          ('workspace', 'market/dates', {**request, 'curve': None}),
                                          ('last', 'market/last-closes', {'session': SESSION}),
                                          ('navigation', 'workspace/navigation', None)]:
                samples = [measure(path, selected) for _ in range(3)]
                report.append({'closes': total, 'request': name, 'samples': [sample for _, sample in samples]})
    workload = json.loads(query(f"""SELECT json_build_object('definitionRevisions',count(DISTINCT p.definition_revision_id),
      'pins',count(*),'uniquePins',count(DISTINCT p.observation_id),'minPins',min(n.pins),'maxPins',max(n.pins),
      'contracts',(SELECT count(DISTINCT m.contract_id) FROM market_data.market_close_members m JOIN market_data.market_closes c ON(c.catalog_id,c.id)=(m.catalog_id,m.close_id) WHERE c.session_code='{SESSION}'))
      FROM market_data.market_close_pins p JOIN market_data.market_closes c ON(c.catalog_id,c.id)=(p.catalog_id,p.close_id)
      JOIN(SELECT close_id,count(*) pins FROM market_data.market_close_pins GROUP BY close_id)n USING(close_id) WHERE c.session_code='{SESSION}';"""))
    memory = {line.split(':')[0]: line.split(':')[1].strip() for line in Path(f'/proc/{api_pid}/status').read_text().splitlines() if line.startswith(('VmRSS:', 'VmHWM:'))}
    print('CALENDAR_MEASUREMENTS=' + json.dumps({'runs': report, 'workload': workload, 'memory': memory,
          'environment': {'platform': platform.platform(), 'postgres': query('SHOW server_version;')}, 'scenarios': 0}), flush=True)
    if profile:profile_native(client, query, request)
    if not verify:
        print('Calendar benchmark only: statement scaling and disclosure are not qualified by this run', flush=True)
        return
    discovery_work_check(client, query, request)
    native_source_work_check(client, query, request)
    native_hidden_work_check(client, query, request)
    hidden_boundaries_check(client, query, manifest, request)
    older_close = query(f"SELECT id FROM market_data.market_closes WHERE session_code='{SESSION}' ORDER BY business_date LIMIT 1;")
    limited_matrix_check(client, query, older_close)
    failed_build_check(client, query, manifest, measure)
    failures = []
    try:require_constant_statements(counts)
    except AssertionError as error:failures.append(str(error))
    print('CALENDAR_SCALING=' + str(counts), flush=True)

    # Hold only observation reads: frame discovery can finish under the gate before execution blocks.
    waiting_sql = "SELECT count(DISTINCT pid) FROM pg_locks WHERE relation='market_data.observations'::regclass AND NOT granted AND mode='AccessShareLock';"
    def start_read(expected=200, relation='observations'):
        outcome = {}
        def work():
            try: outcome['answer'] = client.call('market/dates', {**body, 'session': SESSION}, expected=expected)
            except Exception as error: outcome['error'] = repr(error)
        worker = threading.Thread(target=work); worker.start()
        deadline = time.monotonic()+8
        while query(waiting_sql.replace("market_data.observations", 'market_data.'+relation)) != '1':
            assert worker.is_alive() and time.monotonic()<deadline, outcome
            time.sleep(.025)
        return worker, outcome
    def concurrent():
        worker, outcome = start_read()
        try:
            client.call('access', timeout=1)
        except Exception:
            failures.append('An ordinary request waits behind a market/dates observation read')
        return worker, outcome
    worker, outcome = holding(psql, environment, 'LOCK market_data.observations IN ACCESS EXCLUSIVE MODE', concurrent)
    worker.join(20); assert not worker.is_alive() and 'error' not in outcome, outcome
    print('CALENDAR_GATE=' + ('blocked' if failures and failures[-1].startswith('An ordinary') else 'released'), flush=True)
    principal = "(SELECT principal_id FROM security.identity_bindings WHERE issuer='https://preview.invalid' AND subject='viewer')"
    grants = json.loads(query(f"SELECT json_agg(json_build_object('id',id,'until',granted_to)) FROM security.role_grants WHERE principal_id={principal};"))
    try:
        def expired():
            query(f"UPDATE security.role_grants SET granted_to=clock_timestamp()+interval '2 seconds' WHERE principal_id={principal};")
            worker, outcome = start_read(expected=403)
            deadline = time.monotonic()+5
            while query(f"SELECT bool_and(granted_to<clock_timestamp()) FROM security.role_grants WHERE principal_id={principal};") != 't':
                assert time.monotonic()<deadline
                time.sleep(.025)
            return worker, outcome
        worker, outcome = holding(psql, environment, 'LOCK market_data.observations IN ACCESS EXCLUSIVE MODE', expired)
        worker.join(20)
        if worker.is_alive() or 'error' in outcome:failures.append('Authority expiry did not return a denial: '+str(outcome))
    finally:
        for grant in grants:
            query(f"UPDATE security.role_grants SET granted_to='{grant['until']}'::timestamptz WHERE id='{grant['id']}';")
    def replacement():
        worker, outcome = start_read(expected=422, relation='rate_curves')
        record_close(query, manifest, SESSION, 2, source)
        return worker, outcome
    worker, outcome = holding(psql, environment, 'LOCK market_data.rate_curves IN ACCESS EXCLUSIVE MODE', replacement)
    worker.join(20)
    assert not worker.is_alive() and 'error' not in outcome and outcome['answer']['code'] == 'close_replaced', outcome
    assert not failures, '; '.join(failures)
    print('PASS calendar read statement count is independent of unselected closes; ordinary requests proceed and expired authority refuses disclosure', flush=True)
