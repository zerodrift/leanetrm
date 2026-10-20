"""Exact published synthetic fixings: full, partial and missing actual observations."""
from fractions import Fraction as F
from uuid import UUID, uuid4
from preview_client import PreviewClient
from preview_fixture import identity, trade_booking
from swap_fixture import REVISION


def run_checks(url, query, manifest):
    run_rls_checks(query,manifest)
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    workspace=c.call('workspace')
    before=query("SELECT count(*)::text||':'||(SELECT count(*) FROM trading.publications)::text FROM trading.trades;")
    rule={'frequency':'monthly','rule':'days_after_period_end','days':46,'dayOfMonth':None,
          'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,
          'implementation':'monthly_payment_date_rules_v1'}

    def draft(at, curve, strike):
        frame=next(f for f in workspace['frames'] if f['at'].startswith(at))
        context={'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'}
        catalog=c.call('swaps/catalog',context)
        index=next(i for i in catalog['indices'] if i['curve']==curve)
        assert index['fixingConvention']=='published_index_observations_v1'
        daily=curve.startswith('daily_')
        return {'context':context,'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity'],
                'quantity':{'value':'100','specification':index['quantityChoices'][0]['specification']},
                'fixedCoefficient':'-1','discounting':False,
                'fixed':{'price':strike,'currency':index['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':rule},
                'floating':{'curve':curve,'index':index['index'],'currency':index['currencyCode'],
                            'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':rule,
                            'resetFrequency':'daily' if daily else 'monthly','averagingMethod':'arithmetic' if daily else 'none'}}

    for day,actual_count in [('01',1),('15',15),('31',31)]:
        at=f'2021-01-{day}T08:00:00'
        # The complete monthly index is known even though some delivery remains.
        monthly=draft(at,'base_forward','3')
        priced=c.call('swaps/price',monthly)
        resets=priced['valuation']['sides'][1]['payments'][0]['resets']
        assert F(priced['valuation']['presentValue'])==50
        assert len(resets)==1 and resets[0]['fixingState']=='Actual' and resets[0]['price']=='3.5'
        retained={**monthly,'context':{**monthly['context'],'knowledgeMode':'retained','contextId':priced['context']['contextId']}}
        risk=c.call('swaps/draft-risk',{'draft':retained,'bumpPolicy':priced['bumpPolicy'],'numericalPolicy':priced['numericalPolicy']})
        assert risk['factors']==[] and risk['scenarioCount']==1
        for curve,strike,pv_per_actual,base_pv in [('daily_gas','3',1,16),('daily_power','40',10,160)]:
            body=draft(at,curve,strike)
            priced=c.call('swaps/price',body)
            resets=priced['valuation']['sides'][1]['payments'][0]['resets']
            assert F(priced['valuation']['presentValue'])==base_pv+pv_per_actual*actual_count
            assert len(resets)==31 and sum(r['fixingState']=='Actual' for r in resets)==actual_count
            assert [r['from'] for r in resets]==[f'2021-01-{d:02d}' for d in range(1,32)]
            assert all(r['weight']=='1' and r['lineage'] for r in resets)
            retained={**body,'context':{**body['context'],'knowledgeMode':'retained','contextId':priced['context']['contextId']}}
            request={'draft':retained,'bumpPolicy':priced['bumpPolicy'],'numericalPolicy':priced['numericalPolicy']}
            risk=c.call('swaps/draft-risk',request)
            assert len(risk['factors'])==31-actual_count and risk['scenarioCount']==1+2*(31-actual_count)
            assert all(F(f['delta'])==F(100,31) and F(f['gamma'])==0 for f in risk['factors'])
            assert c.call('swaps/draft-risk',request)['factors']==risk['factors']
            c.call('swaps/price',{**body,'floating':{**body['floating'],'firstFixingAt':'2020-12-31T12:00:00Z'}},expected=422)
        print(f'PASS published index fixings: January {day}; one monthly actual, {actual_count}/31 daily actuals, exact PV and only unresolved forward risk',flush=True)
    # The omitted actual still exists in the database. Exact snapshot pins must
    # prevent the loader finding it through an unrestricted observation read.
    missing=draft('2021-01-15T09:00:00','daily_gas','3')
    error=c.call('swaps/price',missing,expected=422)
    assert error['code']=='index_fixing_missing'
    assert before==query("SELECT count(*)::text||':'||(SELECT count(*) FROM trading.publications)::text FROM trading.trades;")
    print('PASS missing due fixing cannot be projected or recovered outside exact snapshot pins; temporary price/risk writes no trades',flush=True)
    run_retry_checks(url,query,manifest,c)


def run_retry_checks(url,query,manifest,client=None):
    """Computed actual selections cannot change an existing command's identity."""
    c=client or PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    frame=next(f for f in c.call('workspace')['frames'] if f['at'].startswith('2021-01-15T08:00:00'))
    context={'snapshotId':frame['id'],'convention':'independent_side_financial_swap_v2','knowledgeMode':'current'}
    catalog=c.call('swaps/catalog',context)
    index=next(i for i in catalog['indices'] if i['curve']=='daily_gas')
    rule={'frequency':'monthly','rule':'days_after_period_end','days':46,'dayOfMonth':None,
          'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,
          'implementation':'monthly_payment_date_rules_v1'}
    body={**context,**trade_booking(),'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],
          'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity'],
          'quantity':'100','fixedCoefficient':'-1','discounting':False,
          'fixed':{'price':'3','currency':index['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':rule},
          'floating':{'curve':'daily_gas','index':index['index'],'currency':index['currencyCode'],
                      'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':rule,
                      'resetFrequency':'daily','averagingMethod':'arithmetic'}}
    original=c.call('swaps/book',body)
    catalog_id=str(UUID(manifest['catalogId']));predecessor=identity('7',7002001)
    correction=str(uuid4())

    def correct(identifier,previous,amount):
        query(f"""INSERT INTO market_data.observations(catalog_id,id,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,source_revision_id,role,publication_state,observed_at,received_at,value,corrects_id)
          SELECT catalog_id,'{identifier}'::uuid,definition_revision_id,series_id,reference_catalog_id,reference_revision_id,source_code,source_event_id,'{identifier}',role,publication_state,observed_at,clock_timestamp(),value+({amount}),'{previous}'::uuid
          FROM market_data.observations WHERE catalog_id='{catalog_id}' AND definition_revision_id='{REVISION}' AND id='{previous}';""")

    # A 0.31 correction contributes exactly 0.01 to the 31-day average.
    correct(correction,predecessor,'.31')
    try:
        # The fresh preview proves that this correction is actually visible;
        # a replay against unchanged derived inputs would not exercise the bug.
        fresh=c.call('swaps/preview',{**body,'operationId':str(uuid4())})
        actual=fresh['valuation']['sides'][1]['payments'][0]['resets'][0]
        assert F(actual['price'])==F('3.63') and actual['fixingState']=='Actual'
        assert any(o['id']==correction for o in actual['lineage'])
        assert c.call('swaps/book',body)==original
        assert c.call('swaps/status',{'operationId':body['operationId']})==original
        c.call('swaps/book',{**body,'quantity':'101'},expected=409)
        c.call('profile',{'profile':'risk'})
        c.call('swaps/book',body,expected=403)
        c.call('profile',{'profile':'trader'})
        print('PASS booking retry after actual correction: identical body retains original result/status, changed terms conflict, current write authority remains required',flush=True)
    finally:
        correct(str(uuid4()),correction,'-.31')


def run_rls_checks(query,manifest):
    catalog,ref,tenant,workspace=(manifest[k] for k in ('catalogId','referenceCatalogId','tenantId','workspaceId'))
    def issue():
        return query("SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context("
                     f"'https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
    def runtime(sql):
        return f"SET SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{issue()}');{sql}"
    query("CREATE POLICY synthetic_extra_read ON market_data.index_fixing_observations FOR SELECT TO openetrm_runtime USING(true);")
    try:
        assert query("SET SESSION AUTHORIZATION preview_runtime;SELECT count(*) FROM market_data.index_fixing_observations;")=='0'
        assert int(query('BEGIN;'+runtime('SELECT count(*) FROM market_data.index_fixing_observations;')+'ROLLBACK;'))>0
        for table,condition in [('market_catalog_scopes',f"catalog_id='{catalog}'"),
                                ('reference_catalog_scopes',f"catalog_id='{ref}' AND tenant_id='{tenant}' AND workspace_id='{workspace}'")]:
            token=issue()
            assert query(f"BEGIN;DELETE FROM security.{table} WHERE {condition};SET SESSION AUTHORIZATION preview_runtime;"
                         f"SELECT security.use_context('{token}');SELECT count(*) FROM market_data.index_fixing_observations;ROLLBACK;")=='0'
    finally:
        query("DROP POLICY synthetic_extra_read ON market_data.index_fixing_observations;")
    print('PASS fixing schedules retain current identity, market scope and reference scope under an additional permissive policy',flush=True)
