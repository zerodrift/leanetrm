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
