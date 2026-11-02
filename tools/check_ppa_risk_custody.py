"""Retained PPA contexts belong to one exact worker attempt, with live service rights."""
import json
from copy import deepcopy
from uuid import uuid4


def run_checks(c, query, manifest, request, ppa_trade):
    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    first = c.call('portfolio/risk/jobs', dict(requestId=str(uuid4()), request=request))
    stored = json.loads(query(f"SELECT row_to_json(j) FROM risk.jobs j WHERE id='{first['id']}';"))
    second_frame = deepcopy(stored['frame'])
    second_frame['ppaReadContexts'] = []
    def literal(value):
        return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"
    token = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
        f"'https://preview.invalid','risk','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
    submitted = json.loads(query(f"BEGIN;SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');"
        f"SELECT security.use_market_context('{catalog}','{stored['context_id']}');SELECT security.use_trading_context('{catalog}','{stored['valuation_at']}');"
        f"SELECT risk.submit('{catalog}','{uuid4()}','{stored['context_id']}','{stored['revision_id']}','{stored['valuation_at']}',{literal(stored['request'])},{literal(second_frame)});COMMIT;"))
    def worker(sql):
        return query('SET SESSION AUTHORIZATION preview_risk_worker;' + sql)
    claims = [json.loads(worker(f"SELECT risk.claim(60,'{stored['request']['engineVersion']}');")) for _ in range(2)]
    by_id = {claim['jobId']: claim for claim in claims}
    a, b = by_id[first['id']], by_id[submitted['jobId']]
    metadata = f"security.risk_ppa_trade_heads('{catalog}',ARRAY[{','.join(repr(book)+'::uuid' for book in request['bookIds'])}],ARRAY['{ppa_trade}'::uuid])"
    def runtime_bind():
        fresh = query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
            f"'https://preview.invalid','risk','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',120);")
        return (f"SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{fresh}');"
                f"SELECT security.use_market_context('{catalog}','{stored['context_id']}');SELECT security.use_trading_context('{catalog}','{stored['valuation_at']}');")
    worker_bind = f"SET SESSION AUTHORIZATION preview_risk_worker;SELECT risk.bind('{a['jobId']}','{a['leaseToken']}');"
    reference = query(f"SELECT reference_catalog_id FROM trading.ppa_trade_revisions WHERE trade_id='{ppa_trade}' LIMIT 1;")
    unrelated = query(f"SELECT trade_id FROM trading.trade_revisions WHERE book_id='{request['bookIds'][0]}' LIMIT 1;")
    unrelated_metadata = metadata.replace(ppa_trade, unrelated)
    assert query('BEGIN;' + runtime_bind() + f'SELECT count(*) FROM {metadata};ROLLBACK;').splitlines()[-1] == '1'
    revoke = f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{reference}' AND tenant_id='{tenant}' AND workspace_id='{workspace}';"
    query('BEGIN;' + runtime_bind() + f"""RESET SESSION AUTHORIZATION;{revoke}
        SET SESSION AUTHORIZATION preview_runtime;
        DO $$BEGIN IF NOT security.trading_access('{catalog}','ReadTrades') OR NOT security.market_access('{catalog}','ReadMarketData')
         OR security.reference_access('{reference}','ReadMarketData') THEN RAISE EXCEPTION 'Reference revocation did not isolate the intended authority';END IF;
         IF EXISTS(SELECT * FROM {unrelated_metadata}) THEN RAISE EXCEPTION 'An unrelated selection returned a PPA identity';END IF;
         IF EXISTS(SELECT * FROM {metadata} h WHERE h ? 'trade_id') THEN RAISE EXCEPTION 'PPA identities survived revoked reference authority';END IF;
         IF NOT EXISTS(SELECT * FROM {metadata} h WHERE h ? '__unavailable_ppa_identity') THEN RAISE EXCEPTION 'Unreadable PPA scope was not marked incomplete';END IF;END$$;
        ROLLBACK;""")
    # Revocation changes the security epoch: an already bound worker must fail
    # its exact-attempt fence, independently of the per-head runtime predicate.
    query('BEGIN;' + worker_bind + f"""RESET SESSION AUTHORIZATION;{revoke}
        SET SESSION AUTHORIZATION preview_risk_worker;
        DO $$BEGIN PERFORM * FROM {metadata};RAISE EXCEPTION 'PPA identities survived revoked worker reference authority';EXCEPTION WHEN insufficient_privilege THEN NULL;END$$;
        ROLLBACK;""")
    for changes, expected in [("lease_token=gen_random_uuid()", 'risk_job_fence'), ("state='cancellation_requested'", 'risk_job_cancelled')]:
        try:
            query('BEGIN;' + worker_bind + f"RESET SESSION AUTHORIZATION;UPDATE risk.jobs SET {changes} WHERE id='{a['jobId']}';SET SESSION AUTHORIZATION preview_risk_worker;SELECT * FROM {metadata};ROLLBACK;")
        except RuntimeError as error:
            assert expected in str(error), str(error)
        else:
            raise AssertionError('A stale or cancelled worker kept reading PPA risk identities')
    print('PASS PPA risk identities withhold unreadable references and fence revoked, stale and cancelled workers', flush=True)
    context = json.loads(query(f"SELECT json_build_object('effective',effective_on,'known',known_at) FROM market_data.temporal_contexts WHERE id='{stored['context_id']}';"))
    capture = f"security.capture_date_context('{catalog}','{context['effective']}','{context['known']}')"
    admitted = worker(f"BEGIN;SELECT risk.bind('{a['jobId']}','{a['leaseToken']}');SELECT {capture};COMMIT;")
    assert admitted.splitlines()[-1] == stored['context_id'], admitted
    try:
        worker(f"BEGIN;SELECT risk.bind('{b['jobId']}','{b['leaseToken']}');SELECT {capture};COMMIT;")
    except RuntimeError as error:
        assert 'knowledge_coverage' in str(error), str(error)
    else:
        raise AssertionError('One job borrowed the other job\'s admitted PPA contexts.')
    print('PASS concurrent same-actor/context jobs read only their own admitted interval contexts', flush=True)
    instrument = query(f"SELECT instrument_id FROM trading.ppa_revision_bindings WHERE trade_id='{ppa_trade}' LIMIT 1;")
    source = manifest['ppa']['series'][manifest['ppa']['assets'][0] + '_60']['forecast']
    terms = f"security.read_ppa_terms('{catalog}','{instrument}')"
    header = f"market_data.read_interval_series_header('{source}')"
    worker(f"BEGIN;SELECT risk.bind('{a['jobId']}','{a['leaseToken']}');SELECT {terms};SELECT {header};COMMIT;")
    query(f"""BEGIN;SET SESSION AUTHORIZATION preview_risk_worker;
        SELECT risk.bind('{a['jobId']}','{a['leaseToken']}');RESET SESSION AUTHORIZATION;
        UPDATE risk.workers SET enabled=false WHERE login='preview_risk_worker';
        SET SESSION AUTHORIZATION preview_risk_worker;
        DO $$BEGIN PERFORM {terms};RAISE EXCEPTION 'PPA terms survived revoked worker authority';EXCEPTION WHEN insufficient_privilege THEN NULL;END$$;
        DO $$BEGIN PERFORM {header};RAISE EXCEPTION 'PPA source metadata survived revoked worker authority';EXCEPTION WHEN insufficient_privilege THEN NULL;END$$;
        ROLLBACK;""")
    print('PASS PPA definer reads reject revoked worker authority while the represented user remains authorized', flush=True)
    for claim in claims:
        c.call('portfolio/risk/jobs/' + claim['jobId'] + '/cancel', {})
        worker(f"SELECT risk.acknowledge_cancel('{claim['jobId']}','{claim['leaseToken']}');")
