"""HTTP checks of volatility surfaces fitted as jobs on the retained risk-job queue and read from their stored revisions, in
the owned exchange example. A synthetic role gives the trader PublishMarketData, so the trader may fit; the risk analyst
reads market data and trades but may not. Fit jobs run in the risk worker exactly as portfolio risk jobs do."""
import json
import threading
import time
from datetime import datetime, timezone
from decimal import Decimal
from uuid import uuid4
from preview_client import PreviewClient

OPTION = 'futures_option_premium_v1'
MODEL = 'smile_quadratic_log_moneyness_v1'
SOLVER_POLICY = 'american_futures_option_crr_v1'
PRODUCT = 'nymex_on'
# The product the failing and revoked fits use, so that neither leaves a stored surface behind.
UNFITTED = 'nymex_lo'
TIMEOUT = 120
# The close fitted here; the storage checks record their synthetic revisions at another business date.
DAY = '2026-10-16'


def run_checks(url, query, manifest, *, run_worker, worker_login):
    base = PreviewClient(url); base.bootstrap()
    c = PreviewClient(url, 'exchange'); c.opener = base.opener; c.bootstrap()
    catalog, tenant, workspace = (manifest[key] for key in ('catalogId', 'tenantId', 'workspaceId'))
    R = f"'{manifest['referenceCatalogId']}','{manifest['referenceRevisionId']}'"
    M = f"'{catalog}','{manifest['revisionId']}'"
    c.call('profile', {'profile': 'trader'})
    frame = next(f for f in c.call('workspace')['frames'] if f['at'].startswith(DAY))

    def check(name, condition, actual=None):
        assert condition, f'{name}: {actual}'
        print('PASS ' + name, flush=True)

    def read(product=PRODUCT, snapshot=None, **context):
        return c.call('vol-surfaces/read', {'context': {'snapshotId': snapshot or frame['id'], 'convention': OPTION, 'knowledgeMode': 'current', **context},
                                            'product': product, 'model': MODEL}, timeout=TIMEOUT)

    def fit(product=PRODUCT, snapshot=None, request=None, model=MODEL, expected=200):
        return c.call('vol-surfaces/fit', {'requestId': request or str(uuid4()), 'snapshotId': snapshot or frame['id'], 'product': product, 'model': model},
                      expected=expected, timeout=TIMEOUT)

    def job(job_id):
        return json.loads(query(f"SELECT json_build_object('state',state,'errorCode',error_code,'result',result)::text FROM risk.jobs WHERE id='{job_id}';"))

    def revisions(product):
        return int(query(f"SELECT count(*) FROM trading.vol_surface_revisions WHERE catalog_id='{catalog}' AND product_code='{product}' AND effective_on='{DAY}';"))

    # The fitter: the trader, with only PublishMarketData added by a synthetic role.
    trader = query(f"SELECT principal_id FROM security.workspace_memberships WHERE(tenant_id,workspace_id,login_handle)=('{tenant}','{workspace}','trader');")
    role, grant = str(uuid4()), str(uuid4())
    query(f"""INSERT INTO security.roles(id,code,name) VALUES('{role}','synthetic_surface_fitter','Synthetic surface fitter');
    INSERT INTO security.role_permissions VALUES('{role}','PublishMarketData');
    INSERT INTO security.role_grants VALUES('{grant}','{trader}','{role}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false);""")

    c.call('profile', {'profile': 'risk'})
    unfitted = read()
    check('not fitted before any fit', (unfitted['state'], unfitted['revision'], unfitted['job'], unfitted['surface'], unfitted['model'], unfitted['solverPolicy'])
          == ('notFitted', None, None, None, MODEL, SOLVER_POLICY), unfitted)
    refused = fit(expected=403)
    check('reader without PublishMarketData sees canFit false and cannot fit', unfitted['canFit'] is False and refused['code'] == 'access_denied', refused)

    c.call('profile', {'profile': 'trader'})
    check('a fitter sees canFit true', read()['canFit'] is True)
    check('unknown model is refused at submit', fit(model='smile_unknown_v1', expected=422)['code'] == 'vol_model_unknown')
    request = str(uuid4())
    first = fit(request=request)
    check('same request id returns the same job', first['state'] == 'queued' and fit(request=request) == first, first)
    check('a second submit while queued returns the queued job', fit() == first and fit(request=request) == first)
    run_worker()
    completed = job(first['jobId'])
    fitted = read()
    surface = fitted['surface']
    check('fit job completes and read returns the stored surface',
          completed['state'] == 'completed' and fitted['state'] == 'fitted' and fitted['canFit'] is True
          and fitted['revision']['id'] == completed['result']['revision'] and completed['result']['created'] is True
          and fitted['revision']['actor'] == trader and fitted['revision']['knownFrom'] and fitted['revision']['inputsRevised'] is False
          and fitted['job'] == {'id': first['jobId'], 'state': 'completed', 'errorCode': None}
          and surface['modelId'] == MODEL and surface['series'] and surface['expiries'] and fitted['snapshotId'] == frame['id']
          and fitted['context']['effectiveOn'] == DAY, (completed, fitted['revision'], fitted['job']))
    measured = completed['result']
    check('the completed job records its series, solves and elapsed time',
          measured['series'] == len(surface['series']) and measured['solves'] == sum(s['series']['outcome'] == 'solved' for s in surface['series']) > 0
          and measured['elapsedMs'] > 0, measured)
    check('the stored surface has exact decimal text and named option rights',
          all(isinstance(s['series']['strike'], str) and s['series']['right'] in ('call', 'put') for s in surface['series'])
          and all(isinstance(v, str) for s in surface['series'] if s['series']['outcome'] == 'solved' for v in (s['series']['marketVolatility'],)),
          surface['series'][0])
    print(f"WORKLOAD volatility surface fit {PRODUCT}: {measured['series']} series, {measured['solves']} solved, {measured['elapsedMs']} ms in the risk worker", flush=True)

    refit = fit()
    run_worker()
    again = job(refit['jobId'])
    check('refit is a no-op', refit['jobId'] != first['jobId'] and again['state'] == 'completed'
          and (again['result']['revision'], again['result']['created']) == (fitted['revision']['id'], False) and revisions(PRODUCT) == 1, again)

    # One option settlement the stored surface pinned, revised by the market data editor.
    revised = query(f"SELECT o.id||'|'||trim_scale(o.value) FROM trading.vol_surface_inputs i JOIN market_data.observations o ON(o.catalog_id,o.id)=(i.catalog_id,i.observation_id) "
                    f"JOIN market_data.series s ON(s.catalog_id,s.revision_id,s.id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
                    f"JOIN market_data.contracts k ON(k.catalog_id,k.revision_id,k.id)=(s.catalog_id,s.revision_id,s.contract_id) "
                    f"WHERE i.revision_id='{fitted['revision']['id']}' AND k.futures_option_series_code IS NOT NULL ORDER BY o.id LIMIT 1;").split('|')
    c.call('profile', {'profile': 'editor'})
    corrected = c.call('corrections', {'snapshotId': frame['id'], 'observationId': revised[0], 'value': str(Decimal(revised[1]) + Decimal('0.001'))})
    c.call('profile', {'profile': 'trader'})
    flagged = read()
    check('revised settlement flags the stored surface', flagged['state'] == 'fitted' and flagged['revision']['inputsRevised'] is True
          and flagged['revision']['id'] == fitted['revision']['id'] and flagged['surface'] == surface, flagged['revision'])

    renewed = fit(snapshot=corrected['frame']['id'])
    run_worker()
    recorded = job(renewed['jobId'])
    current = read(snapshot=corrected['frame']['id'])
    earlier = read(knowledgeMode='retained', contextId=flagged['context']['contextId'])
    check('refit after revision records a new revision', recorded['state'] == 'completed' and recorded['result']['created'] is True
          and current['revision']['id'] == recorded['result']['revision'] != fitted['revision']['id'] and current['revision']['inputsRevised'] is False
          and revisions(PRODUCT) == 2, (recorded, current['revision']))
    check('the old cutoff still reads the old revision', earlier['revision']['id'] == fitted['revision']['id'] and earlier['surface'] == surface, earlier['revision'])

    # One discount-curve point the stored surface pinned, revised by the market data editor: the fit discounts each series to
    # its expiry, so the point is one of its inputs.
    rate = query(f"SELECT o.id||'|'||trim_scale(o.value) FROM trading.vol_surface_inputs i JOIN market_data.observations o ON(o.catalog_id,o.id)=(i.catalog_id,i.observation_id) "
                 f"JOIN market_data.rate_curve_points p ON(p.catalog_id,p.revision_id,p.series_id)=(o.catalog_id,o.definition_revision_id,o.series_id) "
                 f"WHERE i.revision_id='{current['revision']['id']}' ORDER BY o.id LIMIT 1;")
    check('the stored surface pins the discount-curve points it used', rate != '', current['revision'])
    rate = rate.split('|')
    c.call('profile', {'profile': 'editor'})
    discounted = c.call('corrections', {'snapshotId': corrected['frame']['id'], 'observationId': rate[0], 'value': str(Decimal(rate[1]) + Decimal('0.0025'))})
    c.call('profile', {'profile': 'trader'})
    stale = read(snapshot=corrected['frame']['id'])
    check('a revised discount-curve point flags the stored surface', stale['revision']['inputsRevised'] is True
          and stale['revision']['id'] == current['revision']['id'], stale['revision'])
    rediscounted = fit(snapshot=discounted['frame']['id'])
    run_worker()
    rerun = job(rediscounted['jobId'])
    latest = read(snapshot=discounted['frame']['id'])
    check('refit after a discount-curve revision records a new revision', rerun['state'] == 'completed' and rerun['result']['created'] is True
          and latest['revision']['id'] == rerun['result']['revision'] != current['revision']['id'] and latest['revision']['inputsRevised'] is False
          and latest['surface'] != current['surface'] and revisions(PRODUCT) == 3, (rerun, latest['revision']))

    c.call('market/context', {'effectiveOn': DAY, 'knownAt': latest['context']['knownAt'], 'mode': 'at', 'selection': 'latest_correction'})
    try:
        alias = next(f for f in c.call('workspace')['frames'] if f['at'] == frame['at'])
        historical = fit(snapshot=alias['id'], expected=422)
    finally:
        c.call('market/current', {})
    check('historical context cannot fit', historical['code'] == 'historical_read_only', historical)

    # The submit operation itself admits only a context captured from current knowledge: one captured at an earlier cutoff,
    # or a current capture that a later capture has superseded, is refused with historical_read_only.
    retained = json.loads(query(f"SELECT json_build_object('request',request,'frame',frame,'revision',revision_id)::text FROM risk.jobs WHERE id='{first['jobId']}';"))

    def admission(capture):
        """Captures contexts in one transaction, binds the last one named `bound`, and submits a fit at it."""
        token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                      f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        body = (f"DECLARE bound uuid;later uuid;BEGIN {capture}PERFORM security.use_market_context('{catalog}',bound);"
                f"PERFORM security.use_trading_context('{catalog}','{frame['at']}');"
                f"PERFORM risk.submit_vol_surface_fit('{catalog}','{uuid4()}',bound,'{retained['revision']}','{frame['at']}',"
                f"'{json.dumps(retained['request'])}'::jsonb,jsonb_set('{json.dumps(retained['frame'])}'::jsonb,'{{contextId}}',to_jsonb(bound::text)));"
                "RAISE EXCEPTION 'admitted';EXCEPTION WHEN SQLSTATE 'P0001' THEN PERFORM set_config('check.outcome',SQLERRM,true);END")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');"
                     f"DO $check$ {body} $check$;SELECT current_setting('check.outcome');ROLLBACK;").splitlines()[-1]
    current_capture = f"bound=security.capture_date_context('{catalog}','{DAY}',NULL);"
    check('the submit operation admits a context captured from current knowledge', admission(current_capture) == 'admitted')
    check('the submit operation refuses a context captured at an earlier cutoff',
          admission(f"bound=security.capture_date_context('{catalog}','{DAY}','{fitted['context']['knownAt']}');") == 'historical_read_only')
    check('the submit operation refuses a current capture a later capture superseded',
          admission(current_capture + f"later=security.capture_date_context('{catalog}','{DAY}',NULL);") == 'historical_read_only')

    # A context captured in an earlier transaction stays current only until the next certification: an admitted submit holds the
    # knowledge clock, so a certification waits for it to commit rather than superseding the context it was admitted at.
    def as_trader(statements):
        token = query("SET SESSION AUTHORIZATION preview_authenticator; SELECT security.issue_context("
                      f"'{manifest['identityIssuer']}','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        return query(f"SET SESSION AUTHORIZATION preview_runtime;BEGIN;SELECT security.use_context('{token}');{statements}").splitlines()[-1]
    earlier = as_trader(f"SELECT security.capture_date_context('{catalog}','{DAY}',NULL);COMMIT;")
    # psql sends each statement on its own, so the holder is found by its application name while it sleeps after the submit.
    held = {}

    def hold():
        try:
            held['admitted'] = as_trader(
                "SET LOCAL application_name='vol_surface_held_submit';"
                f"SELECT security.use_market_context('{catalog}','{earlier}');SELECT security.use_trading_context('{catalog}','{frame['at']}');"
                f"SELECT risk.submit_vol_surface_fit('{catalog}','{uuid4()}','{earlier}','{retained['revision']}','{frame['at']}','{json.dumps(retained['request'])}'::jsonb,"
                f"jsonb_set('{json.dumps(retained['frame'])}'::jsonb,'{{contextId}}',to_jsonb('{earlier}'::text)))->>'jobId';SELECT pg_sleep(3);ROLLBACK;SELECT 'held';")
        except Exception as error:
            held['error'] = error
    holding = threading.Thread(target=hold)
    holding.start()
    deadline = time.monotonic() + 10
    while query("SELECT count(*) FROM pg_stat_activity WHERE application_name='vol_surface_held_submit' AND wait_event='PgSleep';") == '0':
        assert time.monotonic() < deadline and holding.is_alive(), held
        time.sleep(.05)
    certification = as_trader("SET LOCAL lock_timeout='500ms';DO $check$ BEGIN "
                              f"PERFORM security.capture_date_context('{catalog}','{DAY}',NULL);PERFORM set_config('check.outcome','certified',true);"
                              "EXCEPTION WHEN lock_not_available THEN PERFORM set_config('check.outcome','waited',true);END $check$;"
                              "SELECT current_setting('check.outcome');ROLLBACK;")
    holding.join()
    check('a certification waits for an admitted submit to commit', certification == 'waited' and held == {'admitted': 'held'}, (certification, held))

    # The close that leaves one option underlying unsettled, fitted for both products at its own business date.
    gap = manifest['missingSettlement']
    unpinned = next(f for f in c.call('workspace')['frames'] if f['at'].replace('+00:00', 'Z') == gap['at'])
    examples = {}
    for product in (PRODUCT, UNFITTED):
        submitted = fit(product=product, snapshot=unpinned['id'])
        run_worker()
        examples[product] = (job(submitted['jobId']), read(product=product, snapshot=unpinned['id'])['surface'])

    def shows_smile(completed, example):
        if completed['state'] != 'completed' or not example:
            return False
        gaps = [e['status'] for e in example['expiries'] if e['underlyingCode'] == gap['underlying']]
        moves = [Decimal(s['richCheap']) for s in example['series'] if s['richCheap'] is not None]
        return (all(e['status'] == 'fitted' for e in example['expiries'] if e['underlyingCode'] != gap['underlying'])
                and gaps in ([], ['vol_fit_insufficient_strikes'])
                and any(m > Decimal('0.005') for m in moves) and any(m < Decimal('-0.005') for m in moves))
    check('example surface shows rich and cheap', all(shows_smile(*examples[p]) for p in examples)
          and any(e['underlyingCode'] == gap['underlying'] for _, s in examples.values() if s for e in s['expiries']),
          {p: (j['state'], j['errorCode'], s and [(e['underlyingCode'], e['status']) for e in s['expiries']]) for p, (j, s) in examples.items()})

    # Every listed series of the other product moved past its expiry: the fit then has no live series to calibrate.
    terms = query(f"SELECT coalesce(json_agg(json_build_object('code',s.code,'day',s.last_trading_date,'at',s.expiry_at,'contract',k.expiry_at)),'[]')::text "
                  f"FROM ref_data.futures_option_series s JOIN market_data.contracts k ON(k.reference_catalog_id,k.reference_revision_id,k.futures_option_series_code)=(s.catalog_id,s.revision_id,s.code) "
                  f"WHERE (s.catalog_id,s.revision_id)=({R}) AND s.product_code='{UNFITTED}' AND (k.catalog_id,k.revision_id)=({M});")

    def expiries(rows):
        query("ALTER TABLE ref_data.futures_option_series DISABLE TRIGGER USER;ALTER TABLE market_data.contracts DISABLE TRIGGER USER;"
              + ''.join(f"UPDATE ref_data.futures_option_series SET last_trading_date='{row['day']}',expiry_at='{row['at']}' WHERE (catalog_id,revision_id,code)=({R},'{row['code']}');"
                        f"UPDATE market_data.contracts SET expiry_at='{row['contract']}' WHERE (catalog_id,revision_id,futures_option_series_code)=({M},'{row['code']}');" for row in rows)
              + "ALTER TABLE ref_data.futures_option_series ENABLE TRIGGER USER;ALTER TABLE market_data.contracts ENABLE TRIGGER USER;")
    original = json.loads(terms)
    assert original
    # The other product's first expiry moved before an earlier close, fitted at that close's own business date so that no
    # surface is left at the close the later checks read: its series are listed as expired, and the others still solve.
    lapsed_day = min(row['day'] for row in original)
    lapsed = [{**row, 'day': '2026-10-13', 'at': '2026-10-13T18:30:00Z', 'contract': '2026-10-13T18:30:00Z'} for row in original if row['day'] == lapsed_day]
    close = next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2026-10-14'))
    expiries(lapsed)
    try:
        partial = fit(product=UNFITTED, snapshot=close['id'])
        run_worker()
    finally:
        expiries(original)
    listed = read(product=UNFITTED, snapshot=close['id'])
    gone = {row['code'] for row in lapsed}
    shown = [s['series'] for s in (listed['surface'] or {'series': []})['series']]
    check('an expired series is listed with option_expired', job(partial['jobId'])['state'] == 'completed'
          and sorted(s['code'] for s in shown) == sorted(row['code'] for row in original)
          and all((s['outcome'], s['settlement'], s['marketVolatility']) == ('option_expired', None, None) for s in shown if s['code'] in gone)
          and any(s['outcome'] == 'solved' for s in shown if s['code'] not in gone)
          # The expiry whose every series expired is labelled so, and only live series' strikes make up the axis.
          and [e['status'] for e in listed['surface']['expiries'] if e['expiryAt'].replace('+00:00', 'Z') == lapsed[0]['at']] == ['option_expired']
          and listed['surface']['strikeAxis'] == sorted({s['strike'] for s in shown if s['code'] not in gone}, key=Decimal),
          (job(partial['jobId']), [(s['code'], s['outcome']) for s in shown if s['code'] in gone]))
    expiries([{**row, 'day': '2026-10-15', 'at': '2026-10-15T18:30:00Z', 'contract': '2026-10-15T18:30:00Z'} for row in original])
    try:
        failing = fit(product=UNFITTED)
        run_worker()
    finally:
        expiries(original)
    failed = read(product=UNFITTED)
    check('a failing fit finishes failed with its code', job(failing['jobId'])['state'] == 'failed' and failed['state'] == 'notFitted'
          and failed['job'] == {'id': failing['jobId'], 'state': 'failed', 'errorCode': 'option_expired'} and revisions(UNFITTED) == 0, failed)

    # The completion of a claimed attempt, called as the worker login. Its digest is one a recording would refuse with
    # vol_surface_input, so a refusal other than the fence's or the authority recheck's means the recording was reached first.
    fenced = fit()
    engine = query(f"SELECT request->>'engineVersion' FROM risk.jobs WHERE id='{fenced['jobId']}';")
    # A short lease, so that the attempt left fenced below expires and is failed within this check.
    claim = json.loads(query(f"SET SESSION AUTHORIZATION {worker_login};SELECT risk.claim(10,'{engine}')::text;"))
    pinned = query(f"SELECT observation_id FROM risk.job_observations WHERE job_id='{fenced['jobId']}' ORDER BY observation_id LIMIT 1;")

    def finish(token):
        try:
            query(f"SET SESSION AUTHORIZATION {worker_login};SELECT risk.finish_vol_surface_fit('{fenced['jobId']}','{token}','{{{pinned}}}',"
                  "'{\"synthetic\":\"surface\"}','not_a_digest','{\"elapsedMs\":1,\"series\":1,\"solves\":1}');")
        except RuntimeError as error:
            return str(error)
        return 'completed'
    wrong = finish(uuid4())
    check('a completion with another lease token is fenced and records nothing', claim['jobId'] == fenced['jobId']
          and 'risk_job_fence' in wrong and revisions(PRODUCT) == 3 and job(fenced['jobId'])['state'] == 'running', wrong)
    query(f"UPDATE security.role_grants SET revoked=true WHERE id='{grant}';")
    lost = finish(claim['leaseToken'])
    check('a completion after the fitter lost PublishMarketData is refused and records nothing',
          'risk_job_authority_changed' in lost and revisions(PRODUCT) == 3 and job(fenced['jobId'])['state'] == 'running', lost)
    # Once its lease expires, the next claim fails the fenced attempt through the queue's own authority-changed path.
    time.sleep(max(0, (datetime.fromisoformat(claim['leaseExpiresAt']) - datetime.now(timezone.utc)).total_seconds()) + 0.5)
    query(f"SET SESSION AUTHORIZATION {worker_login};SELECT risk.claim(10,'{engine}');")
    check('the fenced attempt is failed once its lease expires', (job(fenced['jobId'])['state'], job(fenced['jobId'])['errorCode'])
          == ('failed', 'risk_job_authority_changed') and revisions(PRODUCT) == 3, job(fenced['jobId']))
    grant = str(uuid4())
    query(f"INSERT INTO security.role_grants VALUES('{grant}','{trader}','{role}','{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false);")

    # The fitter loses PublishMarketData after submitting and before the worker runs; this also restores the trader's own roles.
    revoked = fit(product=UNFITTED)
    query(f"UPDATE security.role_grants SET revoked=true WHERE id='{grant}';")
    run_worker()
    after = read(product=UNFITTED)
    check('revoked fitter\'s job records nothing', after['state'] == 'notFitted' and after['canFit'] is False and revisions(UNFITTED) == 0
          and after['job'] == {'id': revoked['jobId'], 'state': 'failed', 'errorCode': 'risk_job_authority_changed'}, after)


def grant_browser_fitter(query, manifest):
    """Gives the trader PublishMarketData again, through the synthetic fitter role run_checks created, for the Vol surfaces
    browser checks, which fit as the trader in a risk worker the preview serves for them."""
    tenant, workspace = manifest['tenantId'], manifest['workspaceId']
    query(f"""INSERT INTO security.role_grants SELECT gen_random_uuid(),m.principal_id,r.id,'{tenant}','{workspace}',clock_timestamp(),'9999-12-31',false
    FROM security.workspace_memberships m,security.roles r
    WHERE (m.tenant_id,m.workspace_id,m.login_handle)=('{tenant}','{workspace}','trader') AND r.code='synthetic_surface_fitter';""")
