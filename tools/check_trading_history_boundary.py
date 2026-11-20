"""A definition cutover cannot present archived trading history as a complete book."""
from uuid import UUID, uuid4
from pathlib import Path
from datetime import datetime, timezone
from native_read_transport import captured_input


def install_proxy(query):
    query("""CREATE FUNCTION public.synthetic_history_boundary(catalog uuid,operation uuid,source uuid) RETURNS void
        LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS
        $$SELECT trading.record_history_boundary($1,$2,$3)$$;""")


def run_checks(query, manifest):
    catalog, tenant, workspace = [str(UUID(manifest[k])) for k in
                                  ('catalogId', 'tenantId', 'workspaceId')]
    book = 'd1000000-0000-0000-0000-000000000001'
    at = '2020-12-18T16:00:00Z'
    def issue(profile='trader'):
        return query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                     f"'https://preview.invalid','{profile}','human_interactive','{tenant}',"
                     f"'{workspace}',NULL,'preview_runtime',60);")
    def runtime(body, profile='trader', before=''):
        return query(f"BEGIN;SET SESSION AUTHORIZATION preview_runtime;"
                     f"SELECT security.use_context('{issue(profile)}');{before}{body} ROLLBACK;")
    def capture():
        return query("SET SESSION AUTHORIZATION preview_runtime;BEGIN;"
                     f"SELECT security.use_context('{issue()}');"
                     f"SELECT security.resolve_operation_context('{catalog}','2020-12-18',NULL);COMMIT;")
    def refused(statement, code, message=None):
        exact = '' if message is None else f"IF SQLERRM<>'{message}' THEN RAISE;END IF;"
        return (f"DO $$BEGIN {statement};RAISE EXCEPTION 'Expected {message or code} refusal';"
                f"EXCEPTION WHEN SQLSTATE '{code}' THEN {exact} END$$;")
    def bind(context):
        return f"SELECT security.use_market_context('{catalog}','{context}');"
    def trade():
        return f"PERFORM security.use_trading_context('{catalog}','{at}')"
    old = capture()
    # An independent later publication makes the earlier certificate incomplete.
    # It contains no financial rows. The caller owns the disposable database;
    # every boundary installation below is rolled back.
    query(f"INSERT INTO trading.publications SELECT '{catalog}',coalesce(max(publication_order),0)+1,"
          f"pg_current_xact_id() FROM trading.publications WHERE catalog_id='{catalog}';")
    current = capture()
    operation = str(uuid4())
    install_proxy(query)
    query("""CREATE FUNCTION public.synthetic_history_boundary_delete() RETURNS void
        LANGUAGE sql SECURITY DEFINER SET search_path=pg_catalog AS
        $$DELETE FROM trading.history_boundaries$$;""")
    install = f"SELECT public.synthetic_history_boundary('{catalog}','{operation}','{current}');"
    runtime(bind(old) + refused(trade(), 'P0001', 'trading_history_archived'), before=install)
    print('PASS trading archive boundary rejects an earlier certified whole-book context', flush=True)
    runtime(bind(current) + f"SELECT security.use_trading_context('{catalog}','{at}');"
            "DO $$BEGIN IF(SELECT count(*) FROM trading.books WHERE id IN("
            "'d1000000-0000-0000-0000-000000000001','d1000000-0000-0000-0000-000000000002'))<>2 "
            "THEN RAISE EXCEPTION 'Current books missing';END IF;END$$;",
            before=install)
    print('PASS trading archive boundary admits its exact complete context', flush=True)
    ppa = f"PERFORM security.ppa_read_scope('{catalog}','books',ARRAY['{book}']::uuid[],'{old}','{at}',false)"
    runtime(refused(ppa, 'P0001', 'trading_history_archived'), before=install)
    print('PASS explicit PPA scope rejects archived history even when it has no PPA rows', flush=True)
    source=captured_input(query,current)
    runtime(refused(f"PERFORM security.register_read_source('{catalog}','{current}','2020-12-18',"
        f"'{source['known_at']}',{source['schedule_prefix']+1},'{source['system_id']}',{source['timeline']})",
        'P0001','temporal_context'),before=install)
    print('PASS a certified cutoff cannot be paired with a manufactured publication prefix',flush=True)
    runtime(bind(old) + "DO $$BEGIN IF NOT EXISTS(SELECT 1 FROM market_data.revisions) OR "
            "NOT EXISTS(SELECT 1 FROM ref_data.units) THEN RAISE EXCEPTION 'Market or reference history missing';END IF;END$$;",
            before=install)
    print('PASS trading archive boundary preserves earlier market and reference reads', flush=True)
    runtime(bind(old) + refused(trade(), '42501'), profile='viewer', before=install)
    runtime(refused(f"PERFORM trading.record_history_boundary('{catalog}','{operation}','{current}')", '42501'))
    runtime(refused(f"INSERT INTO trading.history_boundaries VALUES('{catalog}','{operation}','{current}')", '42501'))
    print('PASS current authority precedes archive refusal and runtime cannot write a boundary', flush=True)
    runtime(f"{install}{install}" + refused(
        f"PERFORM public.synthetic_history_boundary('{catalog}','{uuid4()}','{current}')", 'P0001',
        'trading_history_boundary_conflict') + refused(
        "DELETE FROM trading.history_boundaries", '42501') + refused(
        "PERFORM public.synthetic_history_boundary_delete()", '55000'))
    runtime(refused(f"PERFORM public.synthetic_history_boundary('{catalog}','{operation}','{old}')",
          'P0001', 'trading_history_boundary_input'))
    query('DROP FUNCTION public.synthetic_history_boundary(uuid,uuid,uuid);')
    query('DROP FUNCTION public.synthetic_history_boundary_delete();')
    print('PASS release boundary retries exactly, rejects incomplete certificates and remains immutable', flush=True)


def run_http_checks(query, manifest, url, owned_root, client=None):
    """Install one test-only boundary in the launcher's owned cluster, then remove it."""
    from preview_client import PreviewClient, initial_frame
    owned_root = Path(owned_root).resolve()
    assert Path(query('SHOW data_directory;')).resolve() == owned_root / 'data'
    assert (owned_root / 'data' / 'postmaster.pid').is_file()
    catalog = str(UUID(manifest['catalogId']))
    assert query('SELECT count(*) FROM trading.history_boundaries;') == '0'
    c = client or PreviewClient(url)
    if client is None: c.bootstrap()
    c.call('profile', {'profile': 'trader'})
    c.call('market/current', {})
    market = c.call('workspace'); frame = initial_frame(market, '2020-12-18')
    current = {'snapshotId': frame['id'], 'convention': 'trading_workspace_v1', 'knowledgeMode': 'current'}
    directory = c.call('trading/directory', current)
    old = directory['context']; book = directory['books'][0]['id']
    ppa_cutoff = datetime.fromisoformat(old['knownAt']).astimezone(timezone.utc).isoformat().replace('+00:00', 'Z')
    retained = {**current, 'knowledgeMode': 'retained', 'contextId': old['contextId']}
    facts = {'context': retained, 'bookIds': [], 'portfolioIds': [], 'offset': 0, 'limit': 64}
    before = c.call('trading/positions', facts)
    view = {'mode': 'trades', 'bookIds': [], 'portfolioIds': [], 'groupBy': [], 'measures': [], 'pivotConvention': None}
    boundary = c.call('trading/directory', current)['context']
    assert boundary['knownAt'] > old['knownAt']
    operation = str(uuid4())
    install_proxy(query)
    from assistant_context import Contexts
    Contexts(query,manifest).runtime('trader',f"""DO $$BEGIN
        IF security.resolve_operation_context('{catalog}','{boundary['effectiveOn']}','{boundary['knownAt']}')<>'{UUID(boundary['contextId'])}'::uuid
        THEN RAISE EXCEPTION 'Boundary coordinates changed';END IF;END$$;
        SELECT public.synthetic_history_boundary('{catalog}','{operation}','{UUID(boundary['contextId'])}');""")
    query('DROP FUNCTION public.synthetic_history_boundary(uuid,uuid,uuid);')
    try:
        cases = [('trading/directory', retained), ('trading/positions', facts),
                 ('trading/blotter', {'context': retained, 'view': view}),
                 ('trading/workspace', {'context': retained, 'view': view, 'bookingCatalog': 'directory'}),
                 ('futures/holdings', {**retained, 'convention': market['futuresConvention']}),
                 ('ppa/directory', {'snapshotId': frame['id'], 'bookIds': [book],
                                    'knownAt': ppa_cutoff, 'offset': 0, 'limit': 64})]
        for path, request in cases:
            error = c.call(path, request, expected=422)
            assert error['code'] == 'trading_history_archived', (path, error)
            print('PASS archived trading HTTP refusal: ' + path, flush=True)
        c.call('profile', {'profile': 'risk'})
        c.select_inputs(old)
        error = c.call('portfolio/risk/catalog', {'context': retained, 'bookIds': [], 'portfolioIds': []}, expected=422)
        assert error['code'] == 'trading_history_archived', error
        print('PASS archived historical risk cannot return a surviving-product subset', flush=True)
        c.call('profile', {'profile': 'viewer'})
        assert c.call('trading/directory', retained, expected=403)['code'] == 'access_denied'
        c.call('profile', {'profile': 'trader'})
        c.select_inputs(boundary)
        exact = {**retained, 'contextId': boundary['contextId']}
        assert c.call('trading/directory', exact)['context'] == boundary
        after = c.call('trading/positions', {**facts, 'context': exact})
        assert after['positions'] == before['positions'] and after['total'] == before['total']
        c.call('trading/blotter', {'context': exact, 'view': view})
        assert c.call('trading/directory', current)['canBook']
        print('PASS exact boundary and current HTTP reads retain facts and current booking authority', flush=True)
        c.call('market/context', {'effectiveOn': '2020-12-18', 'knownAt': old['knownAt'],
                                 'mode': 'at', 'selection': 'latest_correction'})
        historical = c.call('workspace')
        assert historical['context']['knownAt'] == old['knownAt'] and historical['definitions'] and historical['references']
        print('PASS archived trading cutoff still supports historical market and reference HTTP reads', flush=True)
    finally:
        # The fixture owns this fresh cluster. Remove only its test marker, never
        # a financial row; the application has no reset or archive bypass route.
        query(f"BEGIN;ALTER TABLE trading.history_boundaries DISABLE TRIGGER immutable_history_boundary;"
              f"DELETE FROM trading.history_boundaries WHERE catalog_id='{catalog}' AND operation_id='{operation}';"
              "ALTER TABLE trading.history_boundaries ENABLE TRIGGER immutable_history_boundary;COMMIT;")
        c.call('market/current', {})
