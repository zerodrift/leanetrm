"""Bounded projection work and authority in retained swap reads."""
import json
from pathlib import Path
from uuid import UUID

from check_booked_side_programs import runtime_query


def run_checks(client, query, manifest):
    client.call('profile', {'profile': 'trader'})
    client.call('market/current', {})
    frame = next(f for f in client.call('workspace/catalog')['frames']
                 if f['at'].startswith('2020-12-18T16:00:00'))
    context = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    view = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [],
            'measures': [], 'pivotConvention': None}
    retained = client.call('trading/workspace', {'context': context, 'view': view,
                                               'bookingCatalog': 'directory'})['catalog']['context']
    catalog = str(UUID(manifest['catalogId']))
    bind = (f"SELECT security.use_market_context('{catalog}','{UUID(retained['contextId'])}');"
            f"SELECT security.use_trading_context('{catalog}','{frame['at']}');")
    statement = (f"SELECT coalesce(jsonb_agg(value ORDER BY value->>'trade_id',value->>'revision'),'[]'::jsonb) "
                 f"FROM trading.read_swap_revisions('{catalog}',NULL,false,ARRAY[]::uuid[])value;")

    def read():
        return json.loads(runtime_query(query, manifest, bind + statement).splitlines()[-1])

    _upgrade(query, read)
    scoped = runtime_query(query, manifest, bind +
                           "SELECT current_setting('join_collapse_limit');" + statement +
                           "SELECT current_setting('join_collapse_limit');",
                           prefix="SET LOCAL join_collapse_limit=7;").splitlines()
    assert scoped[-3] == scoped[-1] == '7' and json.loads(scoped[-2]) == read()
    print('PASS swap projection planning leaves the caller configuration unchanged', flush=True)
    query('ANALYZE;')
    expected = read()
    assert expected and all('__missing_swap_revision' not in r for r in expected)
    _projection_rows(query, manifest, bind, statement, expected)
    literal = lambda value: "'" + value.replace("'", "''") + "'"
    selected = expected[0]
    quote_catalog = str(UUID(selected['reference_catalog_id']))
    quote_revision = str(UUID(selected['reference_revision_id']))
    query("CREATE POLICY synthetic_swap_quote_missing ON ref_data.quote_specifications AS RESTRICTIVE "
          "FOR SELECT TO openetrm_runtime USING(NOT(catalog_id='" + quote_catalog + "'::uuid AND revision_id='" +
          quote_revision + "'::uuid AND code=" + literal(selected['quote_code']) + "));")
    try:
        hidden = json.loads(runtime_query(query, manifest, bind + statement.replace('NULL,false,', 'NULL,true,')).splitlines()[-1])
        assert any(r.get('__missing_swap_revision') for r in hidden), 'A hidden required quote lost the missing-revision refusal.'
        assert not any(r.get('trade_id') == selected['trade_id'] for r in hidden), 'A different quote replaced the hidden exact quote.'
        print('PASS swap projection refuses a hidden required quote without substituting another quote', flush=True)
    finally:
        query('DROP POLICY synthetic_swap_quote_missing ON ref_data.quote_specifications;')
    assert read() == expected, 'Quote visibility checks changed retained terms.'


def _projection_rows(query, manifest, bind, statement, expected):
    """Contracts and weights come from the same selected projection rows."""
    query("CREATE FUNCTION public.synthetic_swap_projection_visit() RETURNS boolean "
          "LANGUAGE plpgsql VOLATILE AS $$BEGIN RETURN true;END$$;")
    tables = ['swap_reset_projections', 'swap_period_projections']
    for table in tables:
        query(f"CREATE POLICY synthetic_swap_projection_visit ON trading.{table} AS RESTRICTIVE "
              "FOR SELECT TO openetrm_runtime USING(public.synthetic_swap_projection_visit());")
    try:
        counted = runtime_query(query, manifest, bind + statement +
                                "SELECT coalesce((SELECT calls FROM pg_stat_xact_user_functions WHERE funcid="
                                "'public.synthetic_swap_projection_visit()'::regprocedure),0);",
                                prefix="SET LOCAL track_functions='all';").splitlines()
        assert json.loads(counted[-2]) == expected
        required = sum(len(row.get('projection_contracts') or []) + len(row.get('first_projection_contracts') or [])
                       for row in expected)
        visits = int(counted[-1])
        assert visits <= required, (visits, required)
        print(f'PASS swap projection reads at most {required} selected rows for both contracts and weights', flush=True)
    finally:
        for table in tables:
            query(f'DROP POLICY synthetic_swap_projection_visit ON trading.{table};')
        query('DROP FUNCTION public.synthetic_swap_projection_visit();')


def _upgrade(query, read):
    """Upgrade populated predecessor data; an unexpected predecessor stays intact."""
    root = Path(__file__).resolve().parents[1]
    original = (root / 'db/migrations/0148_swap_program_admission.sql').read_text()
    start = original.index('CREATE VIEW trading.swap_revision_projection')
    previous = original[start:original.index('ALTER VIEW trading.swap_revision_projection', start)]
    previous = previous.replace('CREATE VIEW', 'CREATE OR REPLACE VIEW', 1)
    migration = (root / 'db/migrations/0159_swap_projection_read_work.sql').read_text()
    function = 'trading.read_swap_revisions(uuid,uuid,boolean,uuid[])'

    def function_state():
        state = json.loads(query(f"SELECT to_jsonb(p) FROM pg_proc p WHERE oid='{function}'::regprocedure;"))
        settings = dict(setting.split('=', 1) for setting in state.pop('proconfig') or [])
        return state, settings

    _, installed_settings = function_state()
    tables = ['swap_trade_revisions', 'swap_instruments', 'swap_fixed_sides', 'swap_floating_sides',
              'swap_reset_projections', 'swap_payment_rules', 'knowledge_receipts']
    fingerprint = "SELECT jsonb_build_object('rows',jsonb_build_array(" + ','.join(
        f"(SELECT md5(coalesce(string_agg(to_jsonb(t)::text,'' ORDER BY to_jsonb(t)::text),'')) FROM trading.{table} t)"
        for table in tables) + "),'policies',(SELECT jsonb_agg(to_jsonb(p) ORDER BY schemaname,tablename,policyname) "\
        "FROM pg_policies p WHERE schemaname IN('trading','ref_data')),'relations',(SELECT jsonb_agg(jsonb_build_array("\
        "oid,relowner,relacl,reloptions,relrowsecurity,relforcerowsecurity) ORDER BY oid) FROM pg_class "\
        "WHERE relnamespace IN('trading'::regnamespace,'ref_data'::regnamespace)));"
    try:
        # The owned fixture already applied 0159. Restore both parts of its
        # predecessor so this check actually qualifies the configuration upgrade.
        query(previous)
        query(f'ALTER FUNCTION {function} RESET join_collapse_limit;')
        before_function, before_settings = function_state()
        expected, unchanged = read(), query(fingerprint)
        query(migration)
        after_function, after_settings = function_state()
        assert after_function == before_function, 'The planner change altered function authority or its definition.'
        assert after_settings == {**before_settings, 'join_collapse_limit': '1'}, after_settings
        assert read() == expected and query(fingerprint) == unchanged
        print('PASS swap projection upgrade preserves projected values, financial rows and authority with only its scoped planner setting', flush=True)
        altered = previous.rstrip().removesuffix(';') + ' WHERE true;'
        query(altered)
        altered_definition = query("SELECT pg_get_viewdef('trading.swap_revision_projection',true);")
        altered_function = function_state()
        try:
            query(migration)
        except RuntimeError as refusal:
            assert 'swap_projection_upgrade_mismatch' in str(refusal), refusal
        else:
            raise AssertionError('The projection upgrade accepted an unexpected predecessor view.')
        assert query("SELECT pg_get_viewdef('trading.swap_revision_projection',true);") == altered_definition
        assert function_state() == altered_function, 'A refused upgrade changed function configuration or authority.'
        assert query(fingerprint) == unchanged
        print('PASS swap projection upgrade refuses an unexpected view without changing its data or authority', flush=True)
    finally:
        try:
            query(previous)
            query(migration)
        finally:
            selected = installed_settings.get('join_collapse_limit')
            query(f'ALTER FUNCTION {function} ' +
                  ('RESET join_collapse_limit;' if selected is None else f'SET join_collapse_limit={int(selected)};'))
