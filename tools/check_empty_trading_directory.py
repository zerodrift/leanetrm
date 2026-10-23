"""Consumer-scoped empty-directory checks in the owned synthetic database."""
from uuid import UUID, uuid4


def run_checks(c,check,query,manifest):
    catalog,reference,tenant,workspace=[str(UUID(manifest[key]))for key in
        ['catalogId','referenceCatalogId','tenantId','workspaceId']]
    assert query('SELECT (SELECT count(*)FROM trading.trade_revisions)+(SELECT count(*)FROM trading.swap_trade_revisions);')=='0', 'Run empty-directory checks before seeding positions'
    c.call('profile',{'profile':'trader'})
    data=c.call('workspace')
    frame=next(f for f in data['frames']if f['at'].startswith('2020-12-18'))
    context={'snapshotId':frame['id'],'convention':'trading_workspace_v1','knowledgeMode':'current'}
    view={'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
    current=c.call('trading/workspace',{'context':context,'view':view,'bookingCatalog':'directory'})
    check('empty working directory retains one exact current trading context',current['catalog']['context']==current['blotter']['context'] and current['catalog']['canBook'])
    retained={**context,'knowledgeMode':'retained','contextId':current['blotter']['context']['contextId']}
    request={'context':retained,'view':view,'bookingCatalog':'directory'}
    initial=c.call('trading/workspace',request)
    legacy=c.call('trading/blotter',{'context':retained,'view':view})
    full=c.call('trading/workspace',{'context':retained,'view':view})
    check('empty directory preserves the exact legacy report and booking metadata',initial['blotter']==legacy==full['blotter']
          and initial['catalog']=={**{k:v for k,v in full['catalog'].items()if k not in ('kind','contracts')},'kind':'directory'})
    for selected_view in [view,{**view,'mode':'pivot','groupBy':['book','instrumentType'],'measures':['quantity'],'pivotConvention':'trading_blotter_totals_v1'}]:
        light=c.call('trading/workspace',{**request,'view':selected_view})
        check('empty directory retains exact typed '+selected_view['mode']+' projection',light['blotter']==c.call('trading/blotter',{'context':retained,'view':selected_view})
              and light['blotter']['holdings']==[] and light['blotter']['groups']==[] and light['blotter']['evaluatedContracts']==0)

    # A requested summary over no positions is the empty projection of its own view, and is validated like any other.
    grouped={**view,'mode':'pivot','groupBy':['book'],'measures':['presentValue','tradeCount'],'pivotConvention':'trading_blotter_totals_v1'}
    own=c.call('trading/blotter',{'context':retained,'view':grouped})
    summarized=c.call('trading/workspace',{**request,'summaries':[grouped,grouped]})
    check('empty directory states each requested summary as the empty projection of its view',
          summarized['summaries']==[{'view':own['view'],'groups':[]}]*2 and own['groups']==[] and initial['summaries']==[]
          and {k:v for k,v in summarized.items()if k!='summaries'}=={k:v for k,v in initial.items()if k!='summaries'})
    for invalid in [[view],[grouped]*5,[{**grouped,'groupBy':['unknown']}]]:
        error=c.call('trading/workspace',{**request,'summaries':invalid},expected=422)
        check('empty directory rejects an invalid summary list',error['code']=='blotter_view')

    # No economic contract is consumed by this explicitly unpriced projection.
    # Withdrawing table access proves it does not secretly materialize the model.
    query('REVOKE SELECT ON market_data.contracts FROM openetrm_runtime;')
    try:
        light=c.call('trading/workspace',request)
        check('proven empty directory does not load unused economic contracts',light==initial)
        c.call('trading/workspace',{**request,'bookingCatalog':'priced_contracts'},expected=403)
        check('the priced catalog still requires its economic definitions')
    finally:query('GRANT SELECT ON market_data.contracts TO openetrm_runtime;')

    pin=str(UUID(frame['observationIds'][0]))
    query(f"CREATE POLICY synthetic_empty_pin_guard ON market_data.observations AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(id<>'{pin}'::uuid);")
    try:
        error=c.call('trading/workspace',request,expected=422)
        check('empty report cannot hide a missing selected observation pin',error['code']=='ambiguous_reference')
    finally:query('DROP POLICY synthetic_empty_pin_guard ON market_data.observations;')
    scope=f"catalog_id='{reference}' AND tenant_id='{tenant}' AND workspace_id='{workspace}'"
    access=query('SELECT access_mode FROM security.reference_catalog_scopes WHERE '+scope+';')
    assert access in ('owner','read_only')
    query('DELETE FROM security.reference_catalog_scopes WHERE '+scope+';')
    try:
        error=c.call('trading/workspace',request,expected=422)
        check('empty report rechecks current exact-reference authority',error['code']=='ambiguous_reference' and set(error)=={'code','message'})
    finally:query(f"INSERT INTO security.reference_catalog_scopes VALUES('{reference}','{tenant}','{workspace}','{access}');")
    book=full['catalog']['books'][0]['id']
    for invalid in [{**view,'bookIds':[str(uuid4())]},{**view,'bookIds':[book,book]},{**view,'mode':'unknown'},
                    {**view,'mode':'pivot','groupBy':['contract'],'measures':['lots'],'pivotConvention':'unknown'}]:
        error=c.call('trading/workspace',{**request,'view':invalid},expected=422)
        check('empty directory rejects invalid view or scope',error['code']=='blotter_view')
    for profile in ['viewer','admin']:
        c.call('profile',{'profile':profile});c.call('trading/workspace',request,expected=403)
        check('an empty result cannot confer Read Trades authority: '+profile)
    c.call('profile',{'profile':'viewer'})


def run_nonempty_checks(c,query,manifest):
    passed=0
    def check(name,condition=True):
        nonlocal passed
        assert condition,name
        passed+=1;print('PASS directory revision guard: '+name,flush=True)
    c.call('profile',{'profile':'trader'});c.call('market/current',{})
    data=c.call('workspace');frame=next(f for f in data['frames']if f['at'].startswith('2020-12-18'))
    view={'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
    context={'snapshotId':frame['id'],'convention':'trading_workspace_v1','knowledgeMode':'current'}
    both=c.call('trading/workspace',{'context':context,'view':view,'bookingCatalog':'directory'})
    assert {r['instrumentType']for r in both['blotter']['holdings']}=={'future','financial_swap'}
    retained={**context,'knowledgeMode':'retained','contextId':both['blotter']['context']['contextId']}
    request={'context':retained,'view':view,'bookingCatalog':'directory'}
    for hidden,visible in [('swap_trade_revisions','future'),('trade_revisions','financial_swap')]:
        query(f'CREATE POLICY synthetic_other_product_guard ON trading.{hidden} AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);')
        try:
            legacy=c.call('trading/blotter',{'context':retained,'view':view})
            light=c.call('trading/workspace',request)
            check('a visible '+visible+' revision retains exact valuation',light['blotter']==legacy and {r['instrumentType']for r in legacy['holdings']}=={visible})
            query('REVOKE SELECT ON market_data.contracts FROM openetrm_runtime;')
            try:
                c.call('trading/workspace',request,expected=403)
                check('a visible '+visible+' revision still requires economic data')
            finally:query('GRANT SELECT ON market_data.contracts TO openetrm_runtime;')
        finally:query(f'DROP POLICY synthetic_other_product_guard ON trading.{hidden};')
    # Missing joined report material must not serve as an emptiness proof.
    query('CREATE POLICY synthetic_join_guard ON trading.trade_references AS RESTRICTIVE FOR SELECT TO openetrm_runtime USING(false);')
    try:
        query('REVOKE SELECT ON market_data.contracts FROM openetrm_runtime;')
        try:
            c.call('trading/workspace',request,expected=403)
            check('failed report joins cannot replace the raw revision guard')
        finally:query('GRANT SELECT ON market_data.contracts TO openetrm_runtime;')
    finally:query('DROP POLICY synthetic_join_guard ON trading.trade_references;')
    print(f'{passed} nonempty directory revision checks passed.',flush=True)
