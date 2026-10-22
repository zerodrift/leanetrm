"""Qualify exact custom graph pins using a completed synthetic preview job."""
from copy import deepcopy
import json
from uuid import uuid4


def run_checks(query, manifest, job_id):
    assert query("SELECT to_regclass('risk.job_custom_curves') IS NOT NULL") == 't', 'Connected risk retention is missing'
    job = json.loads(query(f"SELECT row_to_json(j)::text FROM risk.jobs j WHERE id='{job_id}';"))
    frame = job['frame']
    assert frame['connectedMarkets'], 'The named consumer must retain a real connected market'
    expected_recipes = sum(len(m['recipes']) for m in frame['connectedMarkets'])
    expected_samples = sum(len(m['samples']) for m in frame['connectedMarkets'])
    expected_observations = sum(len(s['observations']) for m in frame['connectedMarkets'] for s in m['samples'])
    for table, count in [('job_custom_curves', expected_recipes), ('job_curve_samples', expected_samples),
                         ('job_curve_sample_observations', expected_observations)]:
        assert query(f"SELECT count(*) FROM risk.{table} WHERE job_id='{job_id}';") == str(count), table

    catalog, tenant, workspace = (manifest[k] for k in ('catalogId', 'tenantId', 'workspaceId'))
    def literal(value):
        return "'" + json.dumps(value).replace("'", "''") + "'::jsonb"

    def token():
        return query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','risk','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")

    def submit(selected, request=None, context=None):
        retained = context or job['context_id']
        authentication = token()
        return query(f"""SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{authentication}');
            SELECT security.use_market_context('{catalog}','{retained}');SELECT security.use_trading_context('{catalog}','{job['valuation_at']}');
            SELECT risk.submit('{catalog}','{uuid4()}','{retained}','{job['revision_id']}','{job['valuation_at']}',
             {literal(request or job['request'])},{literal(selected)});ROLLBACK;""")

    def reject(action, expected):
        try:
            action()
        except RuntimeError as error:
            assert expected in str(error), str(error)
        else:
            raise AssertionError(f'Accepted invalid retained graph: expected {expected}')

    # Re-admit the identical evidence under a fresh operation to prove this is a
    # real accepted baseline, then change one independent pin per rejection.
    submit(frame)
    changes = [
        (lambda f: f['connectedMarkets'][0].update(manifestId=''), 'risk_graph_input'),
        (lambda f: f['connectedMarkets'][0]['recipes'][0].update(version=64), 'custom_projection'),
        (lambda f: f['connectedMarkets'][0]['samples'][0].update(sourceCurve='missing_source'), 'risk_graph_input'),
        (lambda f: f['connectedMarkets'][0]['samples'][0].update(freezeHistorical=not f['connectedMarkets'][0]['samples'][0]['freezeHistorical']), 'risk_graph_input'),
        (lambda f: f['connectedMarkets'][0]['samples'][0].update(observations=[]), 'risk_graph_input'),
        (lambda f: f['connectedMarkets'][0]['samples'][0]['observations'][0].update(seriesId=str(uuid4())), 'risk_job_denied'),
        (lambda f: f['connectedMarkets'][0]['samples'][0]['observations'][0].update(use='Inferred'), 'risk_job_denied'),
    ]
    for change, expected in changes:
        altered = deepcopy(frame)
        change(altered)
        reject(lambda: submit(altered), expected)
    recipe = frame['connectedMarkets'][0]['recipes'][0]
    before = query(f"""SELECT k.known_from-interval '1 microsecond' FROM market_data.custom_curve_versions v
      JOIN market_data.custom_curve_knowledge_receipts k ON(k.catalog_id,k.publication_order,k.publication_xid)=(v.catalog_id,v.publication_order,v.publication_xid)
      WHERE(v.catalog_id,v.curve_id,v.version)=('{catalog}','{recipe['curveId']}',{recipe['version']});""")
    assert before, 'The retained custom version must have a certified publication receipt'
    old_context = query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token()}');SELECT security.capture_date_context('{catalog}','{job['valuation_at'][:10]}','{before}');COMMIT;")
    historical = deepcopy(frame)
    historical['contextId'] = old_context
    for market in historical['supplementalMarkets'].values():
        market['contextId'] = old_context
    historical_request = deepcopy(job['request'])
    historical_request['context']['contextId'] = old_context
    reject(lambda: submit(historical, historical_request, old_context), 'custom_projection')
    reject(lambda: query(f"BEGIN;UPDATE risk.job_curve_samples SET freeze_historical=NOT freeze_historical WHERE job_id='{job_id}';ROLLBACK;"), 'immutable')
    assert query("SELECT NOT has_table_privilege('openetrm_runtime','risk.job_curve_samples','SELECT') AND NOT has_table_privilege('openetrm_risk_worker','risk.job_curve_samples','INSERT');") == 't'
    print('PASS connected risk DB: exact version/source/observation/use retention, historical freezing and publication cutoff, immutable pins and private rows', flush=True)
