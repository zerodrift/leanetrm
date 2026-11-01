"""Exact quote reads in the authorized retained swap projection."""
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
    query('ANALYZE;')
    counted = runtime_query(query, manifest, bind + statement +
                            "SELECT coalesce((SELECT calls FROM pg_stat_xact_user_functions WHERE funcid="
                            "'security.reference_version_visible(uuid,uuid)'::regprocedure),0);",
                            prefix="SET LOCAL track_functions='all';").splitlines()
    expected, checked = json.loads(counted[-2]), int(counted[-1])
    assert expected and all('__missing_swap_revision' not in r for r in expected)
    # Two independent floating sides may each consume a quote. An ordinary
    # projection must not authorize every unrelated quote for every revision.
    assert checked <= 2 * len(expected), (checked, len(expected))
    print(f'PASS swap projection checks {checked} quotes for {len(expected)} selected revisions', flush=True)
    _projection_rows(query, manifest, bind, statement, expected)
    reference = str(UUID(manifest['referenceCatalogId']))
    revision = str(UUID(manifest['referenceRevisionId']))
    quotes = {r['quote_code'] for r in expected}
    literal = lambda value: "'" + value.replace("'", "''") + "'"
    unused = query(f"SELECT code FROM ref_data.quote_specifications WHERE catalog_id='{reference}' "
                   f"AND revision_id='{revision}' AND code NOT IN({','.join(map(literal, sorted(quotes)))}) "
                   "ORDER BY code LIMIT 1;")
    assert unused, 'The synthetic projection check needs an unrelated quote.'
    query(f"""CREATE FUNCTION public.synthetic_swap_quote_guard(code text) RETURNS boolean
      LANGUAGE plpgsql VOLATILE AS $$BEGIN
      IF code={literal(unused)} THEN RAISE EXCEPTION 'Unrelated synthetic swap quote inspected' USING ERRCODE='42501';END IF;
      RETURN true;END$$;
      CREATE POLICY synthetic_swap_quote_guard ON ref_data.quote_specifications AS RESTRICTIVE
      FOR SELECT TO openetrm_runtime USING(public.synthetic_swap_quote_guard(code));""")
    try:
        assert read() == expected, 'An unrelated quote changed the complete retained swap projection.'
        print('PASS swap projection reads only the exact quotes its selected sides name', flush=True)
    finally:
        query('DROP POLICY synthetic_swap_quote_guard ON ref_data.quote_specifications;'
              'DROP FUNCTION public.synthetic_swap_quote_guard(text);')

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
    migration = (root / 'db/migrations/0158_swap_exact_quote_projection.sql').read_text()
    tables = ['swap_trade_revisions', 'swap_instruments', 'swap_fixed_sides', 'swap_floating_sides',
              'swap_reset_projections', 'swap_payment_rules', 'knowledge_receipts']
    fingerprint = "SELECT jsonb_build_object('rows',jsonb_build_array(" + ','.join(
        f"(SELECT md5(coalesce(string_agg(to_jsonb(t)::text,'' ORDER BY to_jsonb(t)::text),'')) FROM trading.{table} t)"
        for table in tables) + "),'policies',(SELECT jsonb_agg(to_jsonb(p) ORDER BY schemaname,tablename,policyname) "\
        "FROM pg_policies p WHERE schemaname IN('trading','ref_data')),'relations',(SELECT jsonb_agg(jsonb_build_array("\
        "oid,relowner,relacl,reloptions,relrowsecurity,relforcerowsecurity) ORDER BY oid) FROM pg_class "\
        "WHERE relnamespace IN('trading'::regnamespace,'ref_data'::regnamespace)));"
    query(previous)
    expected, unchanged = read(), query(fingerprint)
    query(migration)
    assert read() == expected and query(fingerprint) == unchanged
    print('PASS exact-quote upgrade preserves every projected value, financial row, policy and relation grant', flush=True)
    altered = previous.rstrip().removesuffix(';') + ' WHERE true;'
    try:
        query(altered)
        altered_definition = query("SELECT pg_get_viewdef('trading.swap_revision_projection',true);")
        try:
            query(migration)
        except RuntimeError as refusal:
            assert 'swap_projection_upgrade_mismatch' in str(refusal), refusal
        else:
            raise AssertionError('The quote upgrade accepted an unexpected predecessor view.')
        assert query("SELECT pg_get_viewdef('trading.swap_revision_projection',true);") == altered_definition
        assert query(fingerprint) == unchanged
        print('PASS exact-quote upgrade refuses an unexpected view without changing its data or authority', flush=True)
    finally:
        query(previous)
        query(migration)
