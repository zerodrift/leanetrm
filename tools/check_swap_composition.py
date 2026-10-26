"""Synthetic checks for independent swap sides, spreads and reviewed par entry."""
from copy import deepcopy
from decimal import Decimal, localcontext, ROUND_HALF_EVEN
from uuid import uuid4
from preview_client import PreviewClient
from preview_fixture import trade_booking


def run_checks(url, query=None, manifest=None):
    c=PreviewClient(url);c.bootstrap();c.call('profile',{'profile':'trader'})
    w=c.call('workspace');f=next(f for f in w['frames'] if f['at'].startswith('2020-12-18'))
    context={'snapshotId':f['id'],'knowledgeMode':'current','convention':'independent_side_financial_swap_v2'}
    catalog=c.call('swaps/catalog',context)
    assert catalog['entryTemplate']['version']=='monthly_18_calendar_days_v1' and catalog['entryTemplate']['firstCoefficient']=='-1' and catalog['entryTemplate']['spread']=='0'
    assert catalog['entryTemplate']['payment']==dict(frequency='monthly',rule='days_after_period_end',days=18,dayOfMonth=None,dayBasis='calendar_days',shortMonth=None,adjustment='unadjusted',calendar=None,implementation='monthly_payment_date_rules_v1')
    daily=next(i for i in catalog['indices'] if i['curve']=='daily_gas')
    monthly=daily
    def side(i,spread,frequency):
        return {'curve':i['curve'],'index':i['index'],'currency':i['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01',
            'paymentSchedule':{'frequency':'monthly','rule':'days_after_period_end','days':18,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'},
            'resetFrequency':frequency,'averagingMethod':'arithmetic' if frequency=='daily' else 'none','spread':spread}
    right=side(daily,'0.2','daily');left=side(monthly,'0.1','daily')
    draft={'context':context,'definitionRevisionId':daily['definitionRevisionId'],'commodity':daily['commodity'],'quantity':{'value':'100','specification':daily['quantityChoices'][0]['specification']},'swapType':'financial_floating_floating','firstCoefficient':'-1','fixedCoefficient':None,'fixed':None,'firstFloating':left,'floating':right,'discounting':False}
    priced=c.call('swaps/price',draft)
    a=Decimal(c.call('swaps/price',{**draft,'firstFloating':{**left,'spread':'0'},'floating':{**right,'spread':'0'}})['valuation']['presentValue'])
    assert Decimal(priced['valuation']['presentValue'])==a+10
    cash_pv=Decimal(0)
    for row in priced['valuation']['sides']:
        for payment in row['payments']:
            assert Decimal(payment['resolvedQuantity'])==100
            assert Decimal(payment['tradeCashAmount'])==Decimal(payment['cashAmount'])*Decimal(row['coefficient'])*100
            cash_pv+=Decimal(payment['tradePresentValue'])
    assert cash_pv==Decimal(priced['valuation']['presentValue'])

    print('PASS swap composition: each floating spread contributes once after averaging',flush=True)
    wide=c.call('swaps/price',{**draft,'floating':{**right,'spread':'1001.003'}});assert Decimal(wide['valuation']['presentValue'])==Decimal('100090.3')
    body={k:v for k,v in draft.items() if k not in ('context','quantity')};body.update(context);body.update(trade_booking());body.update(quantity='100',bookId=catalog['books'][0]['id'],operationId=str(uuid4()))
    preview=c.call('swaps/preview',body);booked=c.call('swaps/book',body)
    assert c.call('swaps/book',body)==booked
    spelling=deepcopy(body);spelling['firstFloating']['spread']='0.10';spelling['floating']['spread']='0.20';assert c.call('swaps/book',spelling)==booked
    detail=c.call('swaps/editor',{'context':context,'tradeId':booked['tradeId'],'revision':None})
    assert detail['valuation']['presentValue']==preview['valuation']['presentValue']==priced['valuation']['presentValue']
    assert detail['state']['terms']['firstFloating']['spread']=='0.1' and detail['state']['terms']['floating']['spread']=='0.2'
    assert [s['kind'] for s in detail['valuation']['sides']]==['floating','floating']
    conflict=deepcopy(body);conflict['firstFloating']['spread']='0.3';c.call('swaps/book',conflict,expected=409)
    amendment={**body,'operationId':str(uuid4()),'tradeId':booked['tradeId'],'expectedRevision':1,'effectiveAt':catalog['asOf'],'reason':'Synthetic spread amendment','firstFloating':{**left,'spread':'0.3'}}
    c.call('swaps/amend',amendment)
    old=c.call('swaps/editor',{'context':context,'tradeId':booked['tradeId'],'revision':1})
    assert old['state']['terms']['firstFloating']['spread']=='0.1' and not old['canAmend']
    print('PASS swap composition: booking replay, conflict and retained spread amendments',flush=True)
    fixed={'price':'','currency':daily['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':right['paymentSchedule']}
    par_draft={**draft,'swapType':'financial_fixed_floating','firstCoefficient':None,'fixedCoefficient':'-1','fixed':fixed,'firstFloating':None,'parRatePolicy':{'implementation':'par_fixed_price_to_even_v1','decimalPlaces':6,'tieRule':'to_even'}}
    par=c.call('swaps/price',par_draft)
    assert par['resolvedFixedPrice']=='3.36' and par['valuation']['presentValue']=='0' and par['parRate']['policy']=='par_fixed_price_to_even_v1'
    assert c.call('swaps/price',{**par_draft,'fixed':{**fixed,'price':'0'},'parRatePolicy':None})['resolvedFixedPrice']=='0'
    c.call('swaps/price',{**par_draft,'parRatePolicy':None},expected=422)
    c.call('swaps/draft-risk',{'draft':par_draft,'bumpPolicy':par['bumpPolicy'],'numericalPolicy':par['numericalPolicy']},expected=422)
    print('PASS swap composition: blank fixed price solves par, explicit zero remains zero, risk requires fixed reviewed terms',flush=True)
    risk=PreviewClient(url);risk.bootstrap();risk.call('profile',{'profile':'risk'});risk.call('swaps/book',{**body,'operationId':str(uuid4())},expected=403)
    incompatible={**draft,'firstFloating':{**left,'currency':'unknown'}};c.call('swaps/price',incompatible,expected=422)
    print('PASS swap composition: write authority and exact side compatibility remain enforced',flush=True)
    # Both occurrences may reference one reusable side without losing either membership.
    zero={**body,'operationId':str(uuid4()),'firstFloating':{**left,'spread':'0'},'floating':{**right,'spread':'0'}}
    z=c.call('swaps/book',zero);zv=c.call('swaps/editor',{'context':context,'tradeId':z['tradeId'],'revision':None})['valuation']
    assert zv['presentValue']=='0' and len(zv['sides'])==2
    # Distinct indices/curves in the same exact definition revision retain their own factors.
    base=next(i for i in catalog['indices'] if i['curve']=='base_forward')
    local=next(i for i in catalog['indices'] if i['curve']=='local_gas')
    distinct={**draft,'definitionRevisionId':base['definitionRevisionId'],'firstFloating':side(base,'0','monthly'),'floating':side(local,'0','monthly')}
    p=c.call('swaps/price',distinct)
    def point(curve):return Decimal(next(p for p in c.call('evaluate',{'snapshotId':f['id'],'curve':curve})['points'] if p['deliveryFrom']=='2021-01-01')['value'])
    assert Decimal(p['valuation']['presentValue'])==(point('local_gas')-point('base_forward'))*100
    r=c.call('swaps/draft-risk',{'draft':{**distinct,'context':{**context,'knowledgeMode':'retained','contextId':p['context']['contextId']}},'bumpPolicy':p['bumpPolicy'],'numericalPolicy':p['numericalPolicy']})
    assert any(Decimal(x['delta'])==0 for x in r['factors']) and any(Decimal(x['delta'])==100 for x in r['factors'])
    print('PASS swap composition: reusable opposing memberships and independent index factor repricing',flush=True)
    par_book={**body,'operationId':str(uuid4()),'swapType':'financial_fixed_floating','firstCoefficient':None,'fixedCoefficient':'-1','firstFloating':None,'fixed':{**fixed,'price':par['resolvedFixedPrice']}}
    pb=c.call('swaps/book',par_book)
    pv=c.call('swaps/editor',{'context':context,'tradeId':pb['tradeId'],'revision':None})
    assert pv['valuation']['presentValue']=='0' and pv['state']['terms']['floating']['spread']=='0.2'
    c.call('swaps/amend',{**par_book,'operationId':str(uuid4()),'tradeId':pb['tradeId'],'expectedRevision':1,'effectiveAt':catalog['asOf'],'reason':'Synthetic zero spread selection','floating':{**right,'spread':'0'}})
    unchanged={**amendment,'operationId':str(uuid4()),'expectedRevision':2,'firstFloating':{**left,'spread':'0.30'}}
    assert c.call('swaps/preview',unchanged)['changeReview']['action']=='keep_booked_terms'
    c.call('swaps/amend',unchanged,expected=422)
    c.call('swaps/amend',{**par_book,'operationId':str(uuid4()),'tradeId':booked['tradeId'],'expectedRevision':2,'effectiveAt':catalog['asOf'],'reason':'Synthetic rejected subtype conversion','floating':{**right,'spread':'0'}},expected=422)
    print('PASS swap composition: reviewed par quote books concrete terms and zero-spread amendments preserve legacy admission',flush=True)
    view={'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
    shared={**context,'convention':'trading_workspace_v1'}
    rows=c.call('trading/blotter',{'context':shared,'view':view})['holdings']
    measures=[field+suffix for field in ('executionPrice','mark','fixedPrice') for suffix in ('Min','Max','Mean')]
    pivot={**view,'mode':'pivot','groupBy':['book','contract'],'measures':measures,'pivotConvention':'trading_blotter_totals_v1','priceNumericalPolicy':'arithmetic_price_mean_to_even_18_v1'}
    groups=c.call('trading/blotter',{'context':shared,'view':pivot})['groups']
    for group in groups:
        members=[r for r in rows if (r['instrumentType'],r['bookId'],r['contractId'],r['definitionRevisionId'],r['quoteMultiplier'])==(group['instrumentType'],group['bookId'],group['contractId'],group['definitionRevisionId'],group['quoteMultiplier'])]
        assert members and group['priceUnit']
        for field in ('executionPrice','mark','fixedPrice'):
            values=[r[field] for r in members]
            if any(v is None for v in values):assert all(group[field+suffix] is None for suffix in ('Min','Max','Mean'));continue
            values=list(map(Decimal,values))
            with localcontext() as numeric:
                numeric.prec=80
                mean=(sum(values)/len(values)).quantize(Decimal('1e-18'),rounding=ROUND_HALF_EVEN)
            assert Decimal(group[field+'Min'])==min(values) and Decimal(group[field+'Max'])==max(values) and Decimal(group[field+'Mean'])==mean
    c.call('trading/blotter',{'context':shared,'view':{**pivot,'priceNumericalPolicy':None}},expected=422)
    print('PASS blotter prices: explicit extrema and arithmetic means match independent decimal oracles with unavailable values and quote partitions',flush=True)
    if query is not None:
        assert query("SELECT bool_and(NOT has_function_privilege('openetrm_runtime',p.oid,'EXECUTE')) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='security' AND p.proname='retain_floating_swap_side';")=='t'
        # Check the exact normalized relationships, including both independent
        # floating/scheduled memberships and the selected contractual factor.
        relationships=query("SELECT a.attname||':'||c.confrelid::regclass::text FROM pg_constraint c JOIN pg_attribute a ON a.attrelid=c.conrelid AND a.attnum=c.conkey[2] WHERE c.conrelid='trading.swap_instruments'::regclass AND c.contype='f' ORDER BY 1;")
        assert set(relationships.splitlines())=={
            'instrument_id:trading.instruments','fixed_side_id:trading.swap_fixed_sides',
            'floating_side_id:trading.swap_floating_sides','first_floating_side_id:trading.swap_floating_sides',
            'publication_order:trading.publications','scheduled_first_id:trading.swap_scheduled_sides',
            'scheduled_second_id:trading.swap_scheduled_sides','quantity_factor_id:trading.swap_quantity_factors'}
        print('PASS swap composition: private side-retention helper and exact-version side foreign keys',flush=True)
    if manifest is not None:check_custom_sides(c,context,query,manifest)



def check_custom_sides(c, context, query, manifest):
    """Different computed parents exercise both floating slots and rational reports."""
    from fractions import Fraction
    c.call('profile',{'profile':'editor'})
    library=c.call('custom-curves')
    versions=[];prices=[]
    for number,count in enumerate((1,2)):
        definition=deepcopy(next(t['definition'] for t in library['templates'] if t['code']=='ng_average'))
        definition.update(code='synthetic_composed_side_'+str(number),name='Synthetic composed side '+str(number))
        for node in definition['nodes']:
            if node['kind']=='source':node['parameters']['curve']='base_forward'
            if node['kind']=='select':node['parameters']['count']='1'
            if node['kind']=='dates':node['parameters'].update(count=str(count),includeContext='true')
        c.call('profile',{'profile':'editor'})
        saved=c.call('custom-curves/save',{'requestId':str(uuid4()),'snapshotId':context['snapshotId'],'definition':definition,'layout':{}})
        c.call('profile',{'profile':'approver'})
        published=c.call('custom-curves/publish',{'requestId':str(uuid4()),'id':saved['id'],'version':saved['version'],'generation':saved['generation']})
        versions.append({'catalogId':manifest['catalogId'],'curveId':published['id'],'version':published['version'],'outputNode':definition['output']})
        preview=c.call('custom-curves/preview',{'snapshotId':context['snapshotId'],'definition':definition})
        prices.append(sum(Fraction(p['price']['value']) for p in preview['inputs'])/len(preview['inputs']))
    composition={'code':'synthetic_pair_combination','name':'Synthetic pair combination','aggregation':'normalized_weighted_mean_v1','alignment':'exact_delivery_intervals_v1',
        'inputs':[{'source':v,'weight':str(n+1)} for n,v in enumerate(versions)]}
    definition=c.call('custom-curves/compose',{'snapshotId':context['snapshotId'],'composition':composition})['definition']
    c.call('profile',{'profile':'editor'})
    saved=c.call('custom-curves/save',{'requestId':str(uuid4()),'snapshotId':context['snapshotId'],'definition':definition,'layout':{}})
    c.call('profile',{'profile':'approver'})
    combined=c.call('custom-curves/publish',{'requestId':str(uuid4()),'id':saved['id'],'version':saved['version'],'generation':saved['generation']})
    c.call('profile',{'profile':'trader'})
    catalog=c.call('swaps/catalog',context)
    index=next(i for i in catalog['indices'] if i['curve']=='base_forward' and i['definitionRevisionId']==manifest['revisionId'])
    def projection(version):return next(p['reference'] for p in index['customProjections'] if p['reference']['curveId']==version['curveId'])
    payment={'frequency':'monthly','rule':'days_after_period_end','days':18,'dayOfMonth':None,'dayBasis':'calendar_days','shortMonth':None,'adjustment':'unadjusted','calendar':None,'implementation':'monthly_payment_date_rules_v1'}
    side={'curve':index['curve'],'index':index['index'],'currency':index['currencyCode'],'deliveryFrom':'2021-01-01','deliveryTo':'2021-02-01','paymentSchedule':payment,'resetFrequency':'monthly','averagingMethod':'none','spread':'0.1','customProjection':projection(versions[0])}
    second={**side,'spread':'0.2','customProjection':projection({'curveId':combined['id']})}
    draft={'context':context,'definitionRevisionId':index['definitionRevisionId'],'commodity':index['commodity'],'quantity':{'value':'100','specification':index['quantityChoices'][0]['specification']},'swapType':'financial_floating_floating','firstCoefficient':'-1','firstFloating':side,'floating':second,'discounting':False}
    combined_price=(prices[0]+2*prices[1])/3
    expected=(combined_price+Fraction('0.2')-prices[0]-Fraction('0.1'))*100
    priced=c.call('swaps/price',draft)
    assert Fraction(priced['valuation']['presentValue'])==expected
    risk=c.call('swaps/draft-risk',{'draft':{**draft,'context':{**context,'knowledgeMode':'retained','contextId':priced['context']['contextId']}},'bumpPolicy':priced['bumpPolicy'],'numericalPolicy':priced['numericalPolicy']})
    assert all(Fraction(f['basePv'])==expected and Fraction(f['gamma'])==0 for f in risk['factors'])
    body={**context,**{k:v for k,v in draft.items() if k not in ('context','quantity')},**trade_booking(),'operationId':str(uuid4()),'bookId':catalog['books'][0]['id'],'quantity':'100'}
    booked=c.call('swaps/book',body)
    assert c.call('swaps/book',body)==booked
    view={'mode':'trades','bookIds':[],'portfolioIds':[],'groupBy':[],'measures':[],'pivotConvention':None}
    shared={**context,'convention':'trading_workspace_v1'}
    holding=next(h for h in c.call('trading/blotter',{'context':shared,'view':view})['holdings'] if h['tradeId']==booked['tradeId'])
    assert holding['fixedPrice'] is None and Fraction(holding['mark'])==combined_price+Fraction('0.2') and Fraction(holding['presentValue'])==expected
    pivot={**view,'mode':'pivot','groupBy':['contract'],'measures':['presentValue','markMin','markMax','markMean'],'pivotConvention':'trading_blotter_totals_v1','priceNumericalPolicy':'arithmetic_price_mean_to_even_18_v1'}
    group=next(g for g in c.call('trading/blotter',{'context':shared,'view':pivot})['groups'] if g['contractId']==holding['contractId'])
    assert Fraction(group['presentValue'])==expected and Fraction(group['markMin'])==Fraction(group['markMax'])==combined_price+Fraction('0.2')
    with localcontext() as numeric:
        numeric.prec=80
        expected_mean=(Decimal(combined_price.numerator)/Decimal(combined_price.denominator)+Decimal('0.2')).quantize(Decimal('1e-18'),rounding=ROUND_HALF_EVEN)
    assert Decimal(group['markMean'])==expected_mean
    print('PASS composed custom sides: independent published ancestors, rational PV/marks and price pivots, frozen draft risk and booking replay',flush=True)
    if query is not None:check_composed_sql(query,manifest,booked['tradeId'],versions[0]['curveId'])


def check_composed_sql(query, manifest, trade_id, source_parent):
    """Probe the fenced command directly, independently of managed preparation."""
    import json
    catalog,tenant,workspace=(manifest[k] for k in ('catalogId','tenantId','workspaceId'))
    original=json.loads(query(f"SELECT request FROM trading.swap_trade_revisions WHERE trade_id='{trade_id}' AND revision=1;"))
    operation=query(f"SELECT operation_id FROM trading.swap_trade_revisions WHERE trade_id='{trade_id}' AND revision=1;")
    def invoke(value, request_id, setup='', runtime_setup=False):
        token=query(f"SET SESSION AUTHORIZATION preview_authenticator;SELECT security.issue_context('https://preview.invalid','trader','human_interactive','{tenant}','{workspace}',NULL,'preview_runtime',60);")
        payload="'"+json.dumps(value).replace("'","''")+"'::jsonb"
        permission="INSERT INTO security.role_permissions SELECT DISTINCT role_id,p FROM security.role_permissions CROSS JOIN unnest(ARRAY['WriteMarketDefinitions','SealMarketDefinitions'])p WHERE permission='BookTrade' ON CONFLICT DO NOTHING;" if runtime_setup else ''
        return query(f"""BEGIN;{permission}{'' if runtime_setup else setup}SET LOCAL SESSION AUTHORIZATION preview_runtime;SELECT security.use_context('{token}');{setup if runtime_setup else ''}
        SELECT security.save_composed_swap('{catalog}','{request_id}',NULL,NULL,(p->>'book')::uuid,(p->>'definition')::uuid,(p->>'referenceCatalog')::uuid,(p->>'referenceRevision')::uuid,p->>'commodity',
         nullif(p->'fixed','null'::jsonb),nullif(p->'firstFloating','null'::jsonb),p->'floating',(p->>'quantity')::numeric,(p->>'coefficient')::numeric,(p->>'discounting')::boolean,
         (p->>'valuation')::timestamptz,(p->>'effective')::timestamptz,p->>'reason',p->>'implementation',p->>'memo',p->'booking','{{}}'::uuid[])
         FROM(SELECT {payload} p)submitted;ROLLBACK;""")
    assert json.loads(invoke(original,operation))['tradeId']==trade_id
    def reject(value, request_id, setup, expected, runtime_setup=False):
        try:invoke(value,request_id,setup,runtime_setup)
        except RuntimeError as error:assert expected in str(error),str(error)
        else:raise AssertionError('Composed SQL accepted '+expected)
    reject(original,operation,f"DELETE FROM security.reference_catalog_scopes WHERE catalog_id='{manifest['referenceCatalogId']}';",'custom_projection')
    # The child has no direct native source; its published ancestor alone names
    # another exact revision. An ID-only/direct-source check would miss it.
    other=json.loads(query(f"SELECT row_to_json(c) FROM(SELECT revision_id,code FROM market_data.curves WHERE catalog_id='{catalog}' AND revision_id<>'{original['definition']}' ORDER BY revision_id,code LIMIT 1)c;"))
    parent,child=str(uuid4()),str(uuid4())
    setup=f"""
    INSERT INTO market_data.custom_curves VALUES('{catalog}','{parent}','synthetic_foreign_ancestor'),('{catalog}','{child}','synthetic_foreign_child');
    INSERT INTO market_data.custom_curve_versions SELECT(jsonb_populate_record(NULL::market_data.custom_curve_versions,to_jsonb(v)||jsonb_build_object('curve_id','{parent}','state','draft','publication_order',NULL,'published_at',NULL,'publication_xid',NULL))).*
      FROM market_data.custom_curve_versions v WHERE(v.catalog_id,v.curve_id,v.version)=('{catalog}','{source_parent}',1);
    INSERT INTO market_data.custom_curve_nodes SELECT catalog_id,'{parent}',version,node_id,kind,parameters FROM market_data.custom_curve_nodes WHERE(catalog_id,curve_id,version)=('{catalog}','{source_parent}',1);
    INSERT INTO market_data.custom_curve_edges SELECT catalog_id,'{parent}',version,node_id,port,source_node FROM market_data.custom_curve_edges WHERE(catalog_id,curve_id,version)=('{catalog}','{source_parent}',1);
    INSERT INTO market_data.custom_curve_node_sources SELECT catalog_id,'{parent}',version,node_id,source_catalog_id,'{other['revision_id']}','{other['code']}' FROM market_data.custom_curve_node_sources WHERE(catalog_id,curve_id,version)=('{catalog}','{source_parent}',1);
    SELECT security.publish_custom_curve('{catalog}','{parent}',1,1);
    INSERT INTO market_data.custom_curve_versions(catalog_id,curve_id,version,generation,state,name,output_node,engine_version,layout) VALUES('{catalog}','{child}',1,1,'draft','Synthetic foreign child','output','custom_curve_recipe_v2','{{}}');
    INSERT INTO market_data.custom_curve_nodes VALUES('{catalog}','{child}',1,'upstream','published_source','{{"selection":"all_output_points_v1"}}'),('{catalog}','{child}',1,'output','output','{{}}');
    INSERT INTO market_data.custom_curve_edges VALUES('{catalog}','{child}',1,'output','values','upstream');
    INSERT INTO market_data.custom_curve_node_published_sources VALUES('{catalog}','{child}',1,'upstream','{catalog}','{parent}',1,'output');
    SELECT security.publish_custom_curve('{catalog}','{child}',1,1);
    """
    for slot in ('firstFloating','floating'):
        changed=deepcopy(original);changed[slot]['customProjection']['curveId']=child
        reject(changed,str(uuid4()),setup,'custom_projection',runtime_setup=True)
    print('PASS composed SQL: successful replay, revoked source authority refusal and transitive wrong-revision projection rejection on either side',flush=True)
